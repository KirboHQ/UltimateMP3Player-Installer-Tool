// Signing, installing and renewing with an Apple ID (isideload, idevice). Two programs are made of it: ump-helper, which the
// installer drives on the computer with the phone on the cable (cable.rs), and the framework inside the iPhone's app, which
// renews and updates itself (phone.rs, ffi.rs). Both talk the same way: one JSON object per line out, plain lines in (the
// password, a code, yes or no). The password is read once and never written anywhere: what stays in the data folder is the
// token Apple gives back, like xtool does.

#[cfg(not(target_os = "ios"))]
mod cable;
#[cfg(feature = "phone")]
mod ffi;
#[cfg(feature = "phone")]
mod phone;

use std::{
    io::BufRead,
    path::{Path, PathBuf},
    process::ExitCode,
    sync::{Arc, Once, RwLock},
    time::{Duration, SystemTime},
};

use isideload::{
    anisette::{AnisetteClientInfo, remote_v3::RemoteV3AnisetteProvider},
    auth::apple_account::{AppToken, AppleAccount, TwoFactorCallbackParams, TwoFactorCallbackResponse},
    dev::{app_ids::AppIdsApi, certificates::DevelopmentCertificate, developer_session::DeveloperSession},
    sideload::{
        SideloaderBuilder,
        builder::{ExtensionsBehavior, ExtensionsBehaviorSelection, MaxCertsBehavior},
        cert_identity::APPLE_ROOT,
        sideloader::Sideloader,
    },
    util::{
        callbacks::{ExtensionsBehaviorCallbackBox, MaxCertsCallbackBox, MaxCertsCallbackFuture},
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
use sha2::{Digest, Sha256};

pub(crate) const USAGE: &str = "usage: ump-helper --data <folder> login <email> | status | logout | devices | app <udid> | install <udid> <ipa> | renew <udid> | place <udid> [fresh] | send <udid> <list> | move <udid> | remove-old <udid> | sidestore <udid> <ipa> [fresh] | pairing <udid>";
pub(crate) const APP: &str = "com.kirbohq.ultimatemp3player";
pub(crate) const SESSION: &str = "session.json";
const ANISETTE: &str = "https://ani.iloader.app";
// The same on the computer and on the phone, or each would ask Apple for a certificate of its own (a free Apple ID has two).
const MACHINE: &str = "UltimateMP3Player-Installer";

#[derive(Serialize, Deserialize)]
pub(crate) struct Session {
    pub(crate) email: String,
    adsid: String,
    token: String,
    duration: u64,
    expiry: u64,
    // Signed in with the anisette server's identity (Apple had turned ours away): the renewals keep using it.
    #[serde(default)]
    spare: bool,
}

pub fn command_line() -> ExitCode {
    // What the libraries say goes to stderr, for the installer's log; stdout stays the conversation.
    tracing_subscriber::fmt()
        .with_writer(std::io::stderr)
        .with_ansi(false)
        .with_max_level(tracing_subscriber::filter::LevelFilter::INFO)
        .init();
    let args: Vec<String> = std::env::args().skip(1).collect();
    let done = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .map_err(Into::into)
        .and_then(|runtime| runtime.block_on(run(&args)));
    match done {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            emit(json!({ "event": "error", "message": e.to_string() }));
            ExitCode::FAILURE
        }
    }
}

// Once per process: on the phone the same one runs it many times.
fn prepare() {
    static ONCE: Once = Once::new();
    ONCE.call_once(|| {
        let _ = rustls::crypto::ring::default_provider().install_default();
        // In place of isideload::init(), which covers only reqwest's own error: its requests fail as reqwest_middleware's.
        let _ = Hooks::new()
            .context_formatter::<reqwest::Error, _>(WithSources)
            .context_formatter::<reqwest_middleware::Error, _>(WithSources)
            .install();
    });
}

pub(crate) async fn run(args: &[String]) -> Result<(), Report> {
    prepare();
    let [flag, data, command, rest @ ..] = args else { bail!("{USAGE}") };
    if flag != "--data" {
        bail!("{USAGE}");
    }
    let data = PathBuf::from(data);
    std::fs::create_dir_all(&data)?;
    match (command.as_str(), rest) {
        ("login", [email]) => login(&data, email).await,
        ("status", []) => status(&data),
        ("logout", []) => {
            let _ = std::fs::remove_file(data.join(SESSION));
            emit(json!({ "event": "done" }));
            Ok(())
        }
        #[cfg(feature = "phone")]
        ("check-here" | "renew-here" | "update-here", _) => phone::run(&data, command, rest).await,
        #[cfg(not(target_os = "ios"))]
        _ => cable::run(&data, command, rest).await,
        #[cfg(target_os = "ios")]
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

async fn account(data: &Path, email: &str, spare: bool) -> Result<AppleAccount, Report> {
    let mut account = AppleAccount::builder(email).anisette_provider(anisette(data, spare)?).build().await?;
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

// Who's on the other side: the installer, through stdin and stdout, unless someone else says so (the app, ffi.rs).
pub(crate) trait Host: Send + Sync {
    fn say(&self, line: &str);
    fn answer(&self) -> Result<String, Report>;
}

pub(crate) static HOST: RwLock<Option<Arc<dyn Host>>> = RwLock::new(None);

fn host() -> Option<Arc<dyn Host>> {
    HOST.read().unwrap_or_else(|poisoned| poisoned.into_inner()).clone()
}

pub(crate) fn emit(value: serde_json::Value) {
    match host() {
        Some(host) => host.say(&value.to_string()),
        None => println!("{value}"),
    }
}

// In whole percents, and only when that changes: the upload reports every piece it sends.
pub(crate) fn stage(name: &str, progress: f64) {
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
    if let Some(host) = host() {
        return host.answer();
    }
    let mut line = String::new();
    if std::io::stdin().lock().read_line(&mut line)? == 0 {
        bail!("No answer: the input was closed");
    }
    Ok(line.trim_end_matches(['\r', '\n']).to_string())
}

pub(crate) fn storage(data: &Path) -> Box<dyn SideloadingStorage> {
    Box::new(FsStorage::new(data.join("isideload")))
}

// Spare: the identity the anisette server hands out, which its maintainer changes when Apple does.
fn anisette(data: &Path, spare: bool) -> Result<RemoteV3AnisetteProvider, Report> {
    let provider = RemoteV3AnisetteProvider::new(ANISETTE, storage(data), "0".to_string())?;
    if spare {
        return Ok(provider);
    }
    // A current Mac, as iloader sent it, because the anisette servers' 10.12.6 makes Apple ask for the code again and again.
    // Written "Mac OS X" like Apple's own frameworks: since 2026-11-19 gsa.apple.com answers 503 to any "<macOS;".
    Ok(provider.set_client_info_override(Some(AnisetteClientInfo {
        client_info: "<Mac15,7> <Mac OS X;27.0;26A5378j> <com.apple.AuthKit/1 (com.apple.akd/1.0)>".to_string(),
        user_agent: "akd/1.0 CFNetwork/811.5.4".to_string(),
    })))
}

// Apple's door turning the identity away at the first request (no password has gone out yet), as on 2026-11-19.
fn turned_away(error: &str) -> bool {
    error.contains("initial login request") && error.contains("server error (503")
}

async fn login(data: &Path, email: &str) -> Result<(), Report> {
    emit(json!({ "event": "ask", "what": "password" }));
    let password = read_line()?;
    let email = email.to_lowercase();
    let sign_in = async |spare| {
        retrying(async || {
            let mut account = account(data, &email, spare).await?;
            account.login(&password, ask_code).await?;
            Ok(account)
        })
        .await
    };
    let (mut account, spare) = match sign_in(false).await {
        Err(e) if turned_away(&e.to_string()) => {
            eprintln!("Apple turned this identity away, trying the anisette server's: {e}");
            (sign_in(true).await?, true)
        }
        done => (done?, false),
    };
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
    let session = Session { email: email.clone(), adsid, token: token.token, duration: token.duration, expiry: token.expiry, spare };
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

pub(crate) async fn developer_session(data: &Path) -> Result<(String, DeveloperSession), Report> {
    let Ok(bytes) = std::fs::read(data.join(SESSION)) else { bail!("Not signed in") };
    let s: Session = serde_json::from_slice(&bytes)?;
    // Built without logging in: the saved token and the same anisette identity are what Apple checks.
    let account = account(data, &s.email, s.spare).await?;
    let token = AppToken { token: s.token, duration: s.duration, expiry: s.expiry };
    let session = DeveloperSession::new(token, s.adsid, account.grandslam_client.clone(), account.anisette_generator.clone());
    Ok((s.email, session))
}

pub(crate) fn sideloader(data: &Path, email: String, session: DeveloperSession) -> Sideloader<MaxCertsCallbackBox, ExtensionsBehaviorCallbackBox> {
    SideloaderBuilder::new(session, email)
        .machine_name(MACHINE.to_string())
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

// ---- Renewing: what keeps an app working is its profile, which Apple makes for 7 days. A new one put on the phone is enough,
// with nothing signed or sent again, as long as it names the certificate the app on the phone was signed with.

pub(crate) struct NewProfile {
    pub bytes: Vec<u8>,
    pub expires: u64,
}

// The inner Err says why this phone's app can't be renewed this way (it has to be installed again instead).
pub(crate) async fn new_profile(data: &Path, bundle: &str, udid: &str) -> Result<Result<NewProfile, String>, Report> {
    let Some(certificate) = storage(data).retrieve_data(&signed_key(udid))? else {
        return Ok(Err("it isn't known which certificate it was signed with".to_string()));
    };
    let (email, session) = developer_session(data).await?;
    let mut loader = sideloader(data, email, session);
    let team = loader.get_team().await?;
    let session = loader.get_dev_session();
    let ids = session.list_app_ids(&team, None).await?;
    let Some(id) = ids.app_ids.iter().find(|id| id.identifier == bundle) else {
        return Ok(Err(format!("{bundle} isn't this Apple ID's")));
    };
    let profile = session.download_team_provisioning_profile(&team, id, None).await?;
    let bytes: Vec<u8> = profile.encoded_profile.into();
    Ok(match usable(&bytes, &certificate, udid) {
        Ok(expires) => Ok(NewProfile { bytes, expires }),
        Err(why) => Err(why),
    })
}

// Whether a profile is for this phone and this certificate, and when it ends.
fn usable(profile: &[u8], certificate: &[u8], udid: &str) -> Result<u64, String> {
    let Some(plist) = profile_plist(profile) else { return Err("Apple's profile can't be read".to_string()) };
    let list = |key: &str| plist.get(key).and_then(|v| v.as_array()).cloned().unwrap_or_default();
    if !list("DeveloperCertificates").iter().any(|c| c.as_data() == Some(certificate)) {
        return Err("it was signed with a certificate that's gone".to_string());
    }
    if !list("ProvisionedDevices").iter().any(|d| d.as_string().is_some_and(|d| d.eq_ignore_ascii_case(udid))) {
        return Err("the new profile isn't for this phone".to_string());
    }
    match expiry(&plist) {
        // A new one lasts 7 days. Less than 6 is the old one handed out again: said to be renewed, it wouldn't be.
        Some(expires) if expires > now() + 6 * 24 * 3600 => Ok(expires),
        _ => Err("the profile isn't a new one".to_string()),
    }
}

// When the app with this bundle id stops working: the latest of the profiles on the phone that are for it.
pub(crate) fn until(profiles: &[Vec<u8>], bundle: &str) -> Option<u64> {
    profiles
        .iter()
        .filter_map(|p| profile_plist(p))
        .filter(|p| {
            p.get("Entitlements")
                .and_then(|e| e.as_dictionary())
                .and_then(|e| e.get("application-identifier"))
                .and_then(|v| v.as_string())
                // (TEAM.bundle: the team's ten characters, a dot, the bundle id)
                .is_some_and(|id| id.split_once('.').is_some_and(|(_, rest)| rest == bundle))
        })
        .filter_map(|p| expiry(&p))
        .max()
}

fn expiry(profile: &plist::Dictionary) -> Option<u64> {
    let date: SystemTime = profile.get("ExpirationDate")?.as_date()?.into();
    Some(date.duration_since(SystemTime::UNIX_EPOCH).ok()?.as_secs())
}

fn now() -> u64 {
    SystemTime::now().duration_since(SystemTime::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}

// A profile is a signed envelope with a plist inside, as it is.
fn profile_plist(profile: &[u8]) -> Option<plist::Dictionary> {
    let find = |what: &[u8], from: usize| profile[from..].windows(what.len()).position(|w| w == what).map(|i| i + from);
    let start = find(b"<?xml", 0)?;
    let end = find(b"</plist>", start)? + b"</plist>".len();
    plist::from_bytes(&profile[start..end]).ok()
}

// Where the certificate that signed the app on a phone is kept.
pub(crate) fn signed_key(udid: &str) -> String {
    format!("signed_{udid}")
}

// Remembers the certificate an app just signed carries: the one in its profile that's made of this Apple ID's key. Nothing found
// is remembered too, as nothing: the next renewal then installs the app again, which is all that's lost.
pub(crate) fn remember_certificate(data: &Path, email: &str, udid: &str, signed: &Path) {
    let store = storage(data);
    let found = || {
        let profile = profile_plist(&std::fs::read(signed.join("embedded.mobileprovision")).ok()?)?;
        let key = store.retrieve_data(&key_of(email)).ok()??;
        let public = public_half(&key)?;
        let all = profile.get("DeveloperCertificates")?.as_array()?;
        all.iter().filter_map(|c| c.as_data()).find(|c| c.windows(public.len()).any(|w| w == public)).map(<[u8]>::to_vec)
    };
    if let Err(e) = store.store_data(&signed_key(udid), &found().unwrap_or_default()) {
        eprintln!("Couldn't note the certificate: {e}");
    }
}

// isideload's own place for an Apple ID's private key.
pub(crate) fn key_of(email: &str) -> String {
    let hash: String = Sha256::digest(email.as_bytes()).iter().map(|b| format!("{b:02x}")).collect();
    format!("{hash}/key")
}

// The public half of an RSA key of 2048 bits (its modulus, then 65537), written the same way inside the private key's DER and
// inside its certificates.
fn public_half(key: &[u8]) -> Option<&[u8]> {
    const MODULUS: [u8; 5] = [0x02, 0x82, 0x01, 0x01, 0x00];
    const EXPONENT: [u8; 5] = [0x02, 0x03, 0x01, 0x00, 0x01];
    const LENGTH: usize = MODULUS.len() + 256 + EXPONENT.len();
    let start = key.windows(MODULUS.len()).position(|w| w == MODULUS)?;
    key.get(start..start + LENGTH).filter(|half| half.ends_with(&EXPONENT))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_a_503_at_the_first_request_tries_the_spare_identity() {
        // Both as the helper printed them on 2026-11-20.
        let door = "Failed to log in to Apple ID\n ● Failed to send initial login request\n ● Received error response from grandslam\n ● HTTP status server error (503 Service Temporarily Unavailable) for url (https://gsa.apple.com/grandslam/GsService2)";
        let answer = "Failed to log in to Apple ID\n ● GrandSlam error during initial login request\n ● Auth error -20970: Your account information was entered incorrectly.";
        assert!(turned_away(door));
        assert!(!turned_away(answer));
        assert!(!turned_away("Failed to get app token: HTTP status server error (503 Service Unavailable)"));
    }

    #[tokio::test]
    async fn a_connection_that_never_opens_is_tried_again_then_fails() {
        let _ = rustls::crypto::ring::default_provider().install_default();
        let e = patient_client().unwrap().get("https://127.0.0.1:9/").send().await.unwrap_err();
        assert!(e.is_connect());
    }

    // A profile as Apple sends it: a plist with an envelope around it.
    fn profile(bundle: &str, days: u64, certificates: &[&[u8]], devices: &[&str]) -> Vec<u8> {
        let expires = SystemTime::now() + Duration::from_secs(days * 24 * 3600);
        let mut entitlements = plist::Dictionary::new();
        entitlements.insert("application-identifier".into(), format!("ABCDE12345.{bundle}").into());
        let mut dict = plist::Dictionary::new();
        dict.insert("Entitlements".into(), entitlements.into());
        dict.insert("ExpirationDate".into(), plist::Value::Date(expires.into()));
        dict.insert("DeveloperCertificates".into(), certificates.iter().map(|c| plist::Value::Data(c.to_vec())).collect::<Vec<_>>().into());
        dict.insert("ProvisionedDevices".into(), devices.iter().map(|d| plist::Value::String(d.to_string())).collect::<Vec<_>>().into());
        let mut bytes = b"\x30\x82envelope".to_vec();
        plist::to_writer_xml(&mut bytes, &dict).unwrap();
        bytes.extend_from_slice(b"\0signature");
        bytes
    }

    #[test]
    fn a_profile_renews_only_the_app_it_can() {
        let good = profile("a.b.TEAM", 7, &[b"other", b"mine"], &["UDID-1"]);
        assert!(usable(&good, b"mine", "udid-1").unwrap() > now() + 6 * 24 * 3600);
        assert!(usable(&good, b"gone", "UDID-1").is_err());
        assert!(usable(&good, b"mine", "UDID-2").is_err());
        assert!(usable(&good, b"", "UDID-1").is_err());
        assert!(usable(&profile("a.b.TEAM", 0, &[b"mine"], &["UDID-1"]), b"mine", "UDID-1").is_err());
        assert!(usable(b"nothing inside", b"mine", "UDID-1").is_err());
    }

    #[test]
    fn the_app_works_until_the_last_of_its_profiles() {
        let all = vec![profile("a.b.TEAM", 2, &[], &[]), profile("a.b.TEAM", 6, &[], &[]), profile("c.d.TEAM", 9, &[], &[]), b"junk".to_vec()];
        let days = |at: u64| (at - now() + 60) / (24 * 3600);
        assert_eq!(until(&all, "a.b.TEAM").map(days), Some(6));
        assert_eq!(until(&all, "c.d.TEAM").map(days), Some(9));
        assert_eq!(until(&all, "b.TEAM"), None);
    }

    #[test]
    fn a_key_and_its_certificate_share_the_public_half() {
        let mut half = vec![0x02, 0x82, 0x01, 0x01, 0x00];
        half.extend((0..256).map(|i| i as u8));
        half.extend([0x02, 0x03, 0x01, 0x00, 0x01]);
        let key = [&b"\x30\x82\x04\xa4\x02\x01\x00"[..], &half, b"\x02\x82private"].concat();
        let certificate = [&b"\x30\x82\x01\x0a"[..], &half, b"name"].concat();
        let found = public_half(&key).unwrap();
        assert_eq!(found, half);
        assert!(certificate.windows(found.len()).any(|w| w == found));
        assert!(public_half(b"short").is_none());
        assert!(public_half(&key[..100]).is_none());
    }
}
