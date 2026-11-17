// The installer's way to the iPhone: signs and installs apps with an Apple ID (isideload) and gives SideStore the pairing
// file it needs (idevice), talking to usbmuxd directly, so Windows needs only Apple Devices. Driven by the installer through
// stdin and stdout: one JSON object per line out, plain lines in (the password, a code, yes or no). The password is read
// once and never written anywhere: what stays in the data folder is the token Apple gives back, like xtool does.

use std::{
    io::BufRead,
    path::{Path, PathBuf},
    process::ExitCode,
    sync::Arc,
    time::Duration,
};

use idevice::{
    IdeviceError, IdeviceService,
    afc::opcode::AfcFopenMode,
    house_arrest::HouseArrestClient,
    installation_proxy::InstallationProxyClient,
    lockdown::LockdownClient,
    provider::UsbmuxdProvider,
    remote_pairing::{RemotePairingLockdownService, RpPairingFile},
    usbmuxd::{UsbmuxdAddr, UsbmuxdConnection},
};
use isideload::{
    anisette::{AnisetteClientInfo, remote_v3::RemoteV3AnisetteProvider},
    auth::apple_account::{AppToken, AppleAccount, TwoFactorCallbackParams, TwoFactorCallbackResponse},
    dev::{certificates::DevelopmentCertificate, developer_session::DeveloperSession, devices::DevicesApi},
    sideload::{
        SideloaderBuilder,
        builder::{ExtensionsBehavior, ExtensionsBehaviorSelection, MaxCertsBehavior},
        cert_identity::APPLE_ROOT,
        install::install_app,
        sideloader::Sideloader,
    },
    util::{
        callbacks::{ExtensionsBehaviorCallbackBox, MaxCertsCallbackBox, MaxCertsCallbackFuture},
        device::IdeviceInfo,
        fs_storage::FsStorage,
        storage::SideloadingStorage,
    },
};
use rootcause::{
    ReportRef,
    hooks::{Hooks, context_formatter::ContextFormatterHook},
    markers::{Local, Uncloneable},
    prelude::*,
};
use serde::{Deserialize, Serialize};
use serde_json::json;

const USAGE: &str = "usage: ump-helper --data <folder> login <email> | status | logout | devices | install <udid> <ipa> | move <udid> | remove-old <udid> | sidestore <udid> <ipa> [fresh] | pairing <udid>";
const APP: &str = "com.kirbohq.ultimatemp3player";
// What the app keeps (Main.cs in its iOS project): the library, settings, covers and lyrics in Application Support, the
// music in Documents. The songs it only streamed (data/cache) come back by themselves, so they stay behind.
const APP_DATA: [&str; 2] = ["/Library/Application Support", "/Documents"];
const LEFT_BEHIND: &str = "/Library/Application Support/data/cache";
const SESSION: &str = "session.json";
const ANISETTE: &str = "https://ani.iloader.app";
// Where SideStore looks for its pairing file: the first is the classic one, the other two its newer names.
const SIDESTORE_FILES: [&str; 3] = ["ALTPairingFile.mobiledevicepairing", "PairingFile_RemoteRP.plist", "PairingFile_Lockdown.plist"];

#[derive(Serialize, Deserialize)]
struct Session {
    email: String,
    adsid: String,
    token: String,
    duration: u64,
    expiry: u64,
}

#[tokio::main]
async fn main() -> ExitCode {
    let _ = rustls::crypto::ring::default_provider().install_default();
    // In place of isideload::init(), which covers only reqwest's own error: its requests fail as reqwest_middleware's.
    let _ = Hooks::new()
        .context_formatter::<reqwest::Error, _>(WithSources)
        .context_formatter::<reqwest_middleware::Error, _>(WithSources)
        .install();
    // What the libraries say goes to stderr, for the installer's log; stdout stays the conversation.
    tracing_subscriber::fmt()
        .with_writer(std::io::stderr)
        .with_ansi(false)
        .with_max_level(tracing_subscriber::filter::LevelFilter::INFO)
        .init();

    let args: Vec<String> = std::env::args().skip(1).collect();
    match run(&args).await {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            emit(json!({ "event": "error", "message": e.to_string() }));
            ExitCode::FAILURE
        }
    }
}

async fn run(args: &[String]) -> Result<(), Report> {
    let [flag, data, command, rest @ ..] = args else { bail!("{USAGE}") };
    if flag != "--data" {
        bail!("{USAGE}");
    }
    let data = PathBuf::from(data);
    std::fs::create_dir_all(&data)?;
    match (command.as_str(), rest) {
        ("login", [email]) => login(&data, email).await,
        ("status", []) => status(&data),
        ("devices", []) => devices().await,
        ("logout", []) => {
            let _ = std::fs::remove_file(data.join(SESSION));
            emit(json!({ "event": "done" }));
            Ok(())
        }
        ("install", [udid, ipa]) => {
            retrying(async || sign_and_install(&data, udid, PathBuf::from(ipa), false).await).await?;
            emit(json!({ "event": "done" }));
            Ok(())
        }
        ("sidestore", [udid, ipa, again @ ..]) => {
            // The pairing first: it may ask the phone to trust this computer, and it fails in seconds rather than after an install.
            // "fresh" when SideStore is being repaired: the pairing made anew, in case iOS forgot the old one (an update, a reset).
            let pairing = pairing_file(&data, udid, again.iter().any(|a| a == "fresh")).await?;
            // Whether it was there already, with its sign-in and its apps: if not, the person has their part on the phone to do.
            let existed = sidestore_bundle(&device(udid).await?).await?.is_some();
            // Increased memory limit: what iloader asks for SideStore too, the setup that's known to work.
            retrying(async || sign_and_install(&data, udid, PathBuf::from(ipa), true).await).await?;
            place_in_sidestore(udid, &pairing).await?;
            emit(json!({ "event": "done", "existed": existed }));
            Ok(())
        }
        ("move", [udid]) => move_data(udid).await,
        ("remove-old", [udid]) => {
            let provider = trusted(udid).await?;
            let (old, _) = the_two_apps(&provider).await?;
            if let Some(old) = &old {
                InstallationProxyClient::connect(&provider).await?.uninstall(old.clone(), None).await?;
            }
            emit(json!({ "event": "done", "removed": old }));
            Ok(())
        }
        ("pairing", [udid]) => {
            // Asked when SideStore lost its pairing (iOS updated, the phone reset): everything made again.
            let pairing = pairing_file(&data, udid, true).await?;
            place_in_sidestore(udid, &pairing).await?;
            emit(json!({ "event": "done" }));
            Ok(())
        }
        _ => bail!("{USAGE}"),
    }
}

// Apple's servers don't always take a connection at the first try (seen on one network: 4 in 10 failed within 3 s while
// other sites took all, and Windows waits 21 s before giving up on one). Apple opens a new connection for almost every
// request, so each one gets a short wait and more tries; and a whole operation that still never reached Apple is tried
// twice more. Anything Apple answered is final.
struct RetryConnect;

#[async_trait::async_trait]
impl reqwest_middleware::Middleware for RetryConnect {
    async fn handle(
        &self,
        req: reqwest::Request,
        ext: &mut http::Extensions,
        next: reqwest_middleware::Next<'_>,
    ) -> reqwest_middleware::Result<reqwest::Response> {
        let mut tries = 1;
        loop {
            let Some(copy) = req.try_clone() else { return next.run(req, ext).await };
            match next.clone().run(copy, ext).await {
                // The connection never opened: nothing reached Apple, so sending it again is safe.
                Err(e) if tries < 5 && (e.is_connect() || e.is_timeout()) => {
                    eprintln!("Couldn't connect to {} (try {tries} of 5), trying again", req.url().host_str().unwrap_or("Apple"));
                    tries += 1;
                }
                done => return done,
            }
        }
    }
}

// isideload's own client (Apple's root added, the headers as Apple writes them), with the short wait and RetryConnect.
fn patient_client() -> Result<reqwest_middleware::ClientWithMiddleware, Report> {
    let client = reqwest::Client::builder()
        .add_root_certificate(reqwest::Certificate::from_der(APPLE_ROOT)?)
        .http1_title_case_headers()
        .connect_timeout(Duration::from_secs(5))
        .build()?;
    Ok(reqwest_middleware::ClientBuilder::new(client).with(RetryConnect).build())
}

async fn account(data: &Path, email: &str) -> Result<AppleAccount, Report> {
    let mut account = AppleAccount::builder(email).anisette_provider(anisette(data)?).build().await?;
    let gs = Arc::get_mut(&mut account.grandslam_client).ok_or_else(|| report!("The connection to Apple is shared"))?;
    gs.client = patient_client()?;
    Ok(account)
}

// Worth another go: Apple out of reach for a moment, or an answer that came back garbled before any password went out (the URL
// bag, the anisette identity: seen from an empty folder, "Failed to parse URL Bag plist", and fine seconds later).
const PASSING: [&str; 3] = ["error sending request", "URL Bag", "anisette"];

async fn retrying<T>(mut work: impl AsyncFnMut() -> Result<T, Report>) -> Result<T, Report> {
    let mut tries = 1;
    loop {
        match work().await {
            Err(e) if tries < 3 && PASSING.iter().any(|p| e.to_string().contains(p)) => {
                eprintln!("Couldn't reach Apple (try {tries} of 3), trying again: {e}");
                tries += 1;
                tokio::time::sleep(Duration::from_secs(1)).await;
            }
            done => return done,
        }
    }
}

// A failed request says only "error sending request": why (no internet, a timeout, a certificate) is in its sources, and
// the installer needs it to tell the person what's wrong.
struct WithSources;

impl<E: std::error::Error + 'static> ContextFormatterHook<E> for WithSources {
    fn display(&self, report: ReportRef<'_, E, Uncloneable, Local>, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", report.format_current_context_unhooked())?;
        let mut source = report.current_context_error_source();
        while let Some(s) = source {
            write!(f, ": {s}")?;
            source = s.source();
        }
        Ok(())
    }
}

fn emit(value: serde_json::Value) {
    println!("{value}");
}

// In whole percents, and only when that changes: the upload reports every piece it sends.
fn stage(name: &str, progress: f64) {
    static LAST: std::sync::Mutex<Option<(String, i64)>> = std::sync::Mutex::new(None);
    let percent = (progress * 100.0).round() as i64;
    let mut last = LAST.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    if last.as_ref().is_some_and(|(n, p)| n == name && *p == percent) {
        return;
    }
    *last = Some((name.to_string(), percent));
    emit(json!({ "event": "stage", "stage": name, "progress": percent as f64 / 100.0 }));
}

fn read_line() -> Result<String, Report> {
    let mut line = String::new();
    if std::io::stdin().lock().read_line(&mut line)? == 0 {
        bail!("No answer: the input was closed");
    }
    Ok(line.trim_end_matches(['\r', '\n']).to_string())
}

fn storage(data: &Path) -> Box<dyn SideloadingStorage> {
    Box::new(FsStorage::new(data.join("isideload")))
}

fn anisette(data: &Path) -> Result<RemoteV3AnisetteProvider, Report> {
    Ok(RemoteV3AnisetteProvider::new(ANISETTE, storage(data), "0".to_string())?
        // What iloader sends too, until the anisette servers report it themselves.
        .set_client_info_override(Some(AnisetteClientInfo {
            client_info: "<Mac15,7> <macOS;27.0;26A5378j> <com.apple.AuthKit/1 (com.apple.akd/1.0)>".to_string(),
            user_agent: "akd/1.0 CFNetwork/811.5.4".to_string(),
        })))
}

async fn login(data: &Path, email: &str) -> Result<(), Report> {
    emit(json!({ "event": "ask", "what": "password" }));
    let password = read_line()?;
    let email = email.to_lowercase();
    let mut account = retrying(async || {
        let mut account = account(data, &email).await?;
        account.login(&password, ask_code).await?;
        Ok(account)
    })
    .await?;
    drop(password);

    // The same token DeveloperSession asks for, kept so the renewals don't need the password again.
    let token = retrying(async || account.get_app_token("xcode.auth").await).await?;
    let adsid = account
        .spd
        .as_ref()
        .and_then(|spd| spd.get("adsid"))
        .and_then(|v| v.as_string())
        .ok_or_else(|| report!("Apple gave no account id"))?
        .to_string();
    let session = Session { email: email.clone(), adsid, token: token.token, duration: token.duration, expiry: token.expiry };
    std::fs::write(data.join(SESSION), serde_json::to_vec(&session)?)?;
    emit(json!({ "event": "done", "email": email }));
    Ok(())
}

async fn ask_code(p: TwoFactorCallbackParams) -> Result<TwoFactorCallbackResponse, Report> {
    let numbers: Vec<_> = p.numbers.iter().map(|n| json!({ "id": n.id, "number": n.number_with_dial_code })).collect();
    emit(json!({ "event": "ask", "what": "code", "sms": p.sms, "failed": p.unknown, "error": p.last_error, "numbers": numbers }));
    let line = read_line()?;
    Ok(match line.trim() {
        "" | "cancel" => TwoFactorCallbackResponse::Abort,
        "resend" => TwoFactorCallbackResponse::ResendCode,
        "devices" => TwoFactorCallbackResponse::SendToDevices,
        s if s.starts_with("sms:") => TwoFactorCallbackResponse::SendSms(s[4..].parse()?),
        code => TwoFactorCallbackResponse::SubmitCode(code.to_string()),
    })
}

fn status(data: &Path) -> Result<(), Report> {
    // shortcut: says who is signed in, not whether Apple still takes the token; an expired one shows up on the next install.
    let email = std::fs::read(data.join(SESSION))
        .ok()
        .and_then(|b| serde_json::from_slice::<Session>(&b).ok())
        .map(|s| s.email);
    emit(json!({ "event": "account", "email": email }));
    Ok(())
}

async fn developer_session(data: &Path) -> Result<(String, DeveloperSession), Report> {
    let Ok(bytes) = std::fs::read(data.join(SESSION)) else { bail!("Not signed in") };
    let s: Session = serde_json::from_slice(&bytes)?;
    // Built without logging in: the saved token and the same anisette identity are what Apple checks.
    let account = account(data, &s.email).await?;
    let token = AppToken { token: s.token, duration: s.duration, expiry: s.expiry };
    let session = DeveloperSession::new(token, s.adsid, account.grandslam_client.clone(), account.anisette_generator.clone());
    Ok((s.email, session))
}

fn sideloader(data: &Path, email: String, session: DeveloperSession) -> Sideloader<MaxCertsCallbackBox, ExtensionsBehaviorCallbackBox> {
    SideloaderBuilder::new(session, email)
        .machine_name("UltimateMP3Player-Installer".to_string())
        .storage(storage(data))
        .extensions_behavior(ExtensionsBehaviorSelection::Fixed(ExtensionsBehavior::ReuseMain))
        .max_certs_behavior(MaxCertsBehavior::Prompt(Box::new(ask_revoke) as MaxCertsCallbackBox))
        .build()
}

// A free Apple ID holds two certificates: when both belong to something else, the person decides whether one goes.
fn ask_revoke(certs: Vec<DevelopmentCertificate>) -> MaxCertsCallbackFuture {
    Box::pin(async move {
        let first = certs.first();
        emit(json!({
            "event": "ask",
            "what": "revoke",
            "name": first.and_then(|c| c.name.clone()),
            "machine": first.and_then(|c| c.machine_name.clone()),
        }));
        let yes = read_line()?.trim().eq_ignore_ascii_case("yes");
        Ok(if yes { first.and_then(|c| c.serial_number.clone()).map(|s| vec![s]) } else { None })
    })
}

// The phones plugged in, with what's needed to pick one: the installer has its own list (Usbmux.cs), this is for trying by hand.
async fn devices() -> Result<(), Report> {
    let mut mux = UsbmuxdConnection::default().await?;
    for found in mux.get_devices().await? {
        let provider = found.to_provider(UsbmuxdAddr::from_env_var()?, "ump-helper");
        let mut name = None;
        let mut ios = None;
        if let Ok(mut lockdown) = LockdownClient::connect(&provider).await {
            name = lockdown.get_value(Some("DeviceName"), None).await.ok().and_then(|v| v.as_string().map(str::to_string));
            ios = lockdown.get_value(Some("ProductVersion"), None).await.ok().and_then(|v| v.as_string().map(str::to_string));
        }
        emit(json!({ "event": "device", "udid": found.udid, "name": name, "ios": ios }));
    }
    Ok(())
}

async fn device(udid: &str) -> Result<UsbmuxdProvider, Report> {
    let mut mux = UsbmuxdConnection::default().await?;
    let found = mux.get_device(udid).await?;
    Ok(found.to_provider(UsbmuxdAddr::from_env_var()?, "ump-helper"))
}

// The phone, once it trusts this computer. Without a pair record, or with one it no longer knows (it was reset), it's asked
// here, as xtool did, and the person answers on the phone; a locked phone can't show the question, so it waits for it to be
// unlocked. The "phone" events say what the screen should ask.
async fn trusted(udid: &str) -> Result<UsbmuxdProvider, Report> {
    let provider = device(udid).await?;
    let mut mux = UsbmuxdConnection::default().await?;
    if let Ok(record) = mux.get_pair_record(udid).await
        && LockdownClient::connect(&provider).await?.start_session(&record).await.is_ok()
    {
        return Ok(provider);
    }
    let host = uuid::Uuid::new_v4().to_string().to_uppercase();
    let buid = mux.get_buid().await?;
    let mut asked = "";
    loop {
        let mut lockdown = LockdownClient::connect(&provider).await?;
        if asked != "trust" {
            asked = "trust";
            emit(json!({ "event": "phone", "what": "trust" }));
        }
        match lockdown.pair(host.clone(), buid.clone(), Some("UltimateMP3Player-Installer")).await {
            Ok(mut record) => {
                record.udid = Some(udid.to_string());
                mux.save_pair_record(udid, record.serialize()?).await?;
                emit(json!({ "event": "phone", "what": "none" }));
                return Ok(provider);
            }
            Err(IdeviceError::PasswordProtected) => {
                if asked != "unlock" {
                    asked = "unlock";
                    emit(json!({ "event": "phone", "what": "unlock" }));
                }
                tokio::time::sleep(Duration::from_secs(1)).await;
            }
            Err(IdeviceError::UserDeniedPairing) => bail!("The iPhone answered Don't Trust"),
            Err(e) => return Err(e.into()),
        }
    }
}

async fn sign_and_install(data: &Path, udid: &str, ipa: PathBuf, more_memory: bool) -> Result<(), Report> {
    let (email, session) = developer_session(data).await?;
    let mut loader = sideloader(data, email, session);
    let provider = trusted(udid).await?;

    stage("Preparing", 0.0);
    let info = IdeviceInfo::from_device(&provider).await?;
    let team = loader.get_team().await?;
    loader.get_dev_session().ensure_device_registered(&team, &info.name, &info.udid, None).await?;

    let (signed, _) = loader
        .sign_app(
            ipa,
            Some(team),
            more_memory,
            Some(|p: f32| {
                stage("Signing", p as f64);
                std::future::ready(())
            }),
            None,
            Some(&info.udid),
        )
        .await?;
    let installed = install_app(&provider, &signed, |p| stage("Installing", p as f64 / 100.0)).await;
    let _ = std::fs::remove_dir_all(&signed);
    installed
}

// What SideStore needs to reach the phone by itself later, through LocalDevVPN: the computer's pairing record (from
// usbmuxd) with the phone's UDID, Wi-Fi debugging turned on, and from iOS 17.4 an RPPairing too. As iloader makes it.
async fn pairing_file(data: &Path, udid: &str, fresh: bool) -> Result<Vec<u8>, Report> {
    stage("Pairing", 0.0);
    let provider = trusted(udid).await?;
    let mut mux = UsbmuxdConnection::default().await?;
    let mut record = mux.get_pair_record(udid).await?;
    record.udid = Some(udid.to_string());

    let mut lockdown = LockdownClient::connect(&provider).await?;
    let version = lockdown
        .get_value(Some("ProductVersion"), None)
        .await?
        .as_string()
        .unwrap_or("0")
        .to_string();
    lockdown.start_session(&record).await?;
    lockdown
        .set_value("EnableWifiDebugging", true.into(), Some("com.apple.mobile.wireless_lockdown"))
        .await?;
    let mut pairing: plist::Dictionary = plist::from_bytes(&record.serialize()?)?;

    if !below(&version, 17, 4) {
        let store = storage(data);
        let key = format!("rppairing_{udid}");
        let bytes = match store.retrieve_data(&key)? {
            Some(saved) if !fresh && !saved.is_empty() => saved,
            _ => {
                let made = new_rppairing(&provider, &host_label(store.as_ref(), udid, fresh)?).await?;
                store.store_data(&key, &made)?;
                made
            }
        };
        let rp: plist::Dictionary = plist::from_bytes(&bytes)?;
        for (k, v) in rp {
            pairing.insert(k, v);
        }
    }

    let mut out = Vec::new();
    plist::to_writer_xml(&mut out, &pairing)?;
    Ok(out)
}

// The name this computer goes by in the RPPairing, the same for a phone each time unless it's made again.
fn host_label(store: &dyn SideloadingStorage, udid: &str, fresh: bool) -> Result<String, Report> {
    let key = format!("host_label_{udid}");
    if !fresh && let Some(saved) = store.retrieve(&key)?.filter(|l| !l.is_empty()) {
        return Ok(saved);
    }
    let label = format!("ump-{}", &uuid::Uuid::new_v4().simple().to_string()[..6]);
    store.store(&key, &label)?;
    Ok(label)
}

async fn new_rppairing(provider: &UsbmuxdProvider, host: &str) -> Result<Vec<u8>, Report> {
    let service = RemotePairingLockdownService::connect(provider).await?;
    let mut client = service.into_client(host)?;
    let mut file = RpPairingFile::generate(host);
    // The fixed PIN of the pairing over the cable, the one iloader gives too.
    client.connect(&mut file, async || "000000".to_string()).await?;
    Ok(file.to_bytes())
}

// SideStore on the phone, found by its name: its bundle id carries the team of whoever signed it.
async fn sidestore_bundle(provider: &UsbmuxdProvider) -> Result<Option<String>, Report> {
    let mut proxy = InstallationProxyClient::connect(provider).await?;
    Ok(proxy
        .get_apps(Some("User"), None)
        .await?
        .into_iter()
        .find(|(_, app)| {
            app.as_dictionary().and_then(|d| d.get("CFBundleDisplayName")).and_then(|v| v.as_string()) == Some("SideStore")
        })
        .map(|(id, _)| id))
}

async fn place_in_sidestore(udid: &str, pairing: &[u8]) -> Result<(), Report> {
    stage("Pairing", 0.5);
    let provider = device(udid).await?;
    let bundle = sidestore_bundle(&provider).await?.ok_or_else(|| report!("SideStore is not on the phone"))?;

    let mut afc = HouseArrestClient::connect(&provider).await?.vend_documents(bundle).await?;
    afc.mk_dir("/Documents/").await?;
    for name in SIDESTORE_FILES {
        let mut file = afc.open(format!("/Documents/{name}"), AfcFopenMode::Wr).await?;
        file.write_entire(pairing).await?;
        file.close().await?;
    }
    Ok(())
}

// The app as xtool named it (XTL-<team>.<id>) and as isideload and SideStore name it (<id>.<team>): two apps to iOS, each
// with its own data.
async fn the_two_apps(provider: &UsbmuxdProvider) -> Result<(Option<String>, Option<String>), Report> {
    let mut proxy = InstallationProxyClient::connect(provider).await?;
    let ids: Vec<String> = proxy.get_apps(Some("User"), None).await?.into_keys().collect();
    let old = ids.iter().find(|id| id.starts_with("XTL-") && id.ends_with(&format!(".{APP}"))).cloned();
    let new = ids.iter().find(|id| id.starts_with(&format!("{APP}."))).cloned();
    Ok((old, new))
}

// Copies the old app's data into the new one, through this computer, and leaves the old app as it is: removing it is
// remove-old, once the new one shows the music. Best with both apps closed, so neither writes meanwhile; files the new
// app has under the same names are replaced (the app fixes the songs' paths itself, its folder having another name).
async fn move_data(udid: &str) -> Result<(), Report> {
    let provider = trusted(udid).await?;
    let (old, new) = the_two_apps(&provider).await?;
    let Some(old) = old else {
        emit(json!({ "event": "done", "moved": false }));
        return Ok(());
    };
    let Some(new) = new else { bail!("The new app isn't on the phone: install it first") };

    stage("Moving", 0.0);
    let mut from = HouseArrestClient::connect(&provider).await?.vend_container(old.clone()).await?;
    let mut to = HouseArrestClient::connect(&provider).await?.vend_container(new.clone()).await?;

    let mut dirs = Vec::new();
    let mut files = Vec::new();
    for root in APP_DATA {
        let mut pending = vec![root.to_string()];
        while let Some(dir) = pending.pop() {
            // A folder the old app never made has nothing to move.
            let Ok(names) = from.list_dir(dir.clone()).await else { continue };
            dirs.push(dir.clone());
            for name in names.into_iter().filter(|n| n != "." && n != "..") {
                let path = format!("{dir}/{name}");
                let info = from.get_file_info(path.clone()).await?;
                match info.st_ifmt.as_str() {
                    "S_IFDIR" if path != LEFT_BEHIND => pending.push(path),
                    "S_IFREG" => files.push((path, info.size)),
                    _ => {}
                }
            }
        }
    }

    let total = files.iter().map(|(_, size)| size).sum::<usize>().max(1);
    let mut copied = 0;
    // Parents come before their folders in the list, so each one has where to go.
    for dir in dirs {
        to.mk_dir(dir).await?;
    }
    for (path, _) in &files {
        let mut src = from.open(path.clone(), AfcFopenMode::RdOnly).await?;
        let mut dst = to.open(path.clone(), AfcFopenMode::Wr).await?;
        loop {
            let chunk = src.read_n(1024 * 1024).await?;
            if chunk.is_empty() {
                break;
            }
            dst.write_entire(&chunk).await?;
            copied += chunk.len();
            stage("Moving", copied as f64 / total as f64);
        }
        src.close().await?;
        dst.close().await?;
    }
    emit(json!({ "event": "done", "moved": true, "from": old, "to": new, "files": files.len(), "bytes": copied }));
    Ok(())
}

fn below(version: &str, major: u32, minor: u32) -> bool {
    let mut parts = version.split('.').map(|p| p.parse::<u32>().unwrap_or(0));
    (parts.next().unwrap_or(0), parts.next().unwrap_or(0)) < (major, minor)
}

#[cfg(test)]
mod tests {
    use super::{below, patient_client};

    #[tokio::test]
    async fn a_connection_that_never_opens_is_tried_again_then_fails() {
        let _ = rustls::crypto::ring::default_provider().install_default();
        let e = patient_client().unwrap().get("https://127.0.0.1:9/").send().await.unwrap_err();
        assert!(e.is_connect());
    }

    #[test]
    fn versions() {
        assert!(below("17.3.1", 17, 4));
        assert!(!below("17.4", 17, 4));
        assert!(!below("26.1", 17, 4));
        assert!(below("9.10", 17, 4));
    }
}
