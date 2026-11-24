// The phone on the cable, through usbmuxd directly, so Windows needs only Apple Devices.

use std::{
    collections::HashMap,
    fs::File,
    io::Read,
    path::{Path, PathBuf},
    time::Duration,
};

use idevice::{
    IdeviceError, IdeviceService,
    afc::opcode::AfcFopenMode,
    house_arrest::HouseArrestClient,
    installation_proxy::InstallationProxyClient,
    lockdown::LockdownClient,
    misagent::MisagentClient,
    provider::UsbmuxdProvider,
    remote_pairing::{RemotePairingLockdownService, RpPairingFile},
    usbmuxd::{UsbmuxdAddr, UsbmuxdConnection},
};
use isideload::{
    dev::devices::DevicesApi,
    sideload::install::install_app,
    util::{device::IdeviceInfo, storage::SideloadingStorage},
};
use rootcause::prelude::*;
use serde::Deserialize;
use serde_json::json;

use crate::*;

// What the app keeps (Main.cs in its iOS project): the library, settings, covers and lyrics in Application Support, the
// music in Documents. The songs it only streamed (data/cache) come back by themselves, so they stay behind.
const APP_DATA: [&str; 2] = ["/Library/Application Support", "/Documents"];
const LEFT_BEHIND: &str = "/Library/Application Support/data/cache";
// Where SideStore looks for its pairing file: the first is the classic one, the other two its newer names.
const SIDESTORE_FILES: [&str; 3] = ["ALTPairingFile.mobiledevicepairing", "PairingFile_RemoteRP.plist", "PairingFile_Lockdown.plist"];
// Where the app looks for what it needs to renew itself: out of Documents, which the Files app shows, and out of Application
// Support, which is what its backups and move_data copy.
const APP_KEYS: &str = "/Library/Renewal";
// Where it looks for what comes from the computer's app, as it starts and as it comes back in front. It takes nothing until the last
// file of the list is there.
const INBOX: &str = "/Library/FromComputer";
// What the phone has to keep free besides.
const SPARE: u64 = 300 * 1024 * 1024;

pub async fn run(data: &Path, command: &str, rest: &[String]) -> Result<(), Report> {
    match (command, rest) {
        ("devices", []) => devices().await,
        ("app", [udid]) => app(udid).await,
        ("install", [udid, ipa]) => {
            retrying(async || sign_and_install(data, udid, PathBuf::from(ipa), false).await).await?;
            emit(json!({ "event": "done" }));
            Ok(())
        }
        ("renew", [udid]) => renew(data, udid).await,
        ("place", [udid, again @ ..]) => place_in_app(data, udid, again.iter().any(|a| a == "fresh")).await,
        ("sidestore", [udid, ipa, again @ ..]) => {
            // The pairing first: it may ask the phone to trust this computer, and it fails in seconds rather than after an install.
            // "fresh" when SideStore is being repaired: the pairing made anew, in case iOS forgot the old one (an update, a reset).
            let pairing = pairing_file(data, udid, again.iter().any(|a| a == "fresh")).await?;
            // Whether it was there already, with its sign-in and its apps: if not, the person has their part on the phone to do.
            let existed = sidestore_bundle(&device(udid).await?).await?.is_some();
            // Increased memory limit: what iloader asks for SideStore too, the setup that's known to work.
            retrying(async || sign_and_install(data, udid, PathBuf::from(ipa), true).await).await?;
            place_in_sidestore(udid, &pairing).await?;
            emit(json!({ "event": "done", "existed": existed }));
            Ok(())
        }
        ("send", [udid, list]) => send(udid, Path::new(list)).await,
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
            let pairing = pairing_file(data, udid, true).await?;
            place_in_sidestore(udid, &pairing).await?;
            emit(json!({ "event": "done" }));
            Ok(())
        }
        _ => bail!("{USAGE}"),
    }
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

async fn knows_this_computer(provider: &UsbmuxdProvider, udid: &str) -> Result<bool, Report> {
    let mut mux = UsbmuxdConnection::default().await?;
    let Ok(record) = mux.get_pair_record(udid).await else { return Ok(false) };
    Ok(LockdownClient::connect(provider).await?.start_session(&record).await.is_ok())
}

// The phone, once it trusts this computer. Without a pair record, or with one it no longer knows (it was reset), it's asked
// here, as xtool did, and the person answers on the phone; a locked phone can't show the question, so it waits for it to be
// unlocked. The "phone" events say what the screen should ask.
async fn trusted(udid: &str) -> Result<UsbmuxdProvider, Report> {
    let provider = device(udid).await?;
    if knows_this_computer(&provider, udid).await? {
        return Ok(provider);
    }
    let mut mux = UsbmuxdConnection::default().await?;
    let host = uuid::Uuid::new_v4().to_string().to_uppercase();
    let buid = mux.get_buid().await?;
    let mut asked = "";
    loop {
        let mut lockdown = LockdownClient::connect(&provider).await?;
        if asked != "trust" {
            asked = "trust";
            emit(json!({ "event": "phone", "what": "trust" }));
        }
        match lockdown.pair(host.clone(), buid.clone(), Some(MACHINE)).await {
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

// What's on the phone, asked quietly: a phone that doesn't trust this computer yet isn't bothered, it just doesn't say.
// "renews": the app can renew itself (its Info.plist says by which version of what place_in_app puts there). LocalDevVPN and
// SideStore are found by their names.
async fn app(udid: &str) -> Result<(), Report> {
    let provider = device(udid).await?;
    if !knows_this_computer(&provider, udid).await? {
        emit(json!({ "event": "app", "trusted": false }));
        return Ok(());
    }
    let apps = InstallationProxyClient::connect(&provider).await?.get_apps(Some("User"), None).await?;
    let text = |app: &plist::Value, key: &str| app.as_dictionary().and_then(|d| d.get(key)).and_then(|v| v.as_string()).map(str::to_string);
    let named = |name: &str| apps.values().any(|app| text(app, "CFBundleDisplayName").as_deref() == Some(name));
    let ours = apps.iter().find(|(id, _)| id.starts_with(&format!("{APP}.")));
    let expires = match ours {
        Some((bundle, _)) => until(&MisagentClient::connect(&provider).await?.copy_all().await?, bundle),
        None => None,
    };
    emit(json!({
        "event": "app",
        "trusted": true,
        "bundle": ours.map(|(id, _)| id),
        "version": ours.and_then(|(_, app)| text(app, "CFBundleShortVersionString")),
        "renews": ours_key(&apps, "UMPRenewal"),
        "imports": ours_key(&apps, "UMPImport"),
        "expires": expires,
        "vpn": apps.contains_key("com.jkcoxson.LocalDevVPN") || named("LocalDevVPN"),
        "sidestore": named("SideStore"),
    }));
    Ok(())
}

// A number the app on the phone says of itself in its Info.plist: what it can do with this program.
fn ours_key(apps: &HashMap<String, plist::Value>, key: &str) -> Option<u64> {
    let (_, app) = apps.iter().find(|(id, _)| id.starts_with(&format!("{APP}.")))?;
    app.as_dictionary()?.get(key)?.as_unsigned_integer()
}

async fn sign_and_install(data: &Path, udid: &str, ipa: PathBuf, more_memory: bool) -> Result<(), Report> {
    let (email, session) = developer_session(data).await?;
    let mut loader = sideloader(data, email.clone(), session);
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
    // (SideStore, the one that asks for more memory, isn't renewed from here)
    if installed.is_ok() && !more_memory {
        remember_certificate(data, &email, udid, &signed);
    }
    let _ = std::fs::remove_dir_all(&signed);
    installed
}

// Seconds instead of a whole install, when it can be (new_profile). "renewed": false means it has to be installed again.
async fn renew(data: &Path, udid: &str) -> Result<(), Report> {
    stage("Renewing", 0.05);
    let not_this_way = |why: &str| {
        eprintln!("Not renewed in place: {why}");
        emit(json!({ "event": "done", "renewed": false }));
        Ok(())
    };
    let provider = trusted(udid).await?;
    let (_, Some(bundle)) = the_two_apps(&provider).await? else { return not_this_way("the app isn't on the phone") };
    stage("Renewing", 0.2);
    let profile = match retrying(async || new_profile(data, &bundle, udid).await).await? {
        Ok(profile) => profile,
        Err(why) => return not_this_way(&why),
    };
    stage("Renewing", 0.8);
    // What's really on the phone is what counts, also if its answer got lost on the way.
    let put = MisagentClient::connect(&provider).await?.install(profile.bytes).await;
    let expires = until(&MisagentClient::connect(&provider).await?.copy_all().await?, &bundle);
    if expires.is_none_or(|e| e < profile.expires) {
        return not_this_way(&put.err().map_or("the phone didn't keep the new profile".to_string(), |e| e.to_string()));
    }
    stage("Renewing", 1.0);
    emit(json!({ "event": "done", "renewed": true, "expires": expires }));
    Ok(())
}

// What the app needs to renew itself, put in its own folder: the sign-in's token, the private key (Apple ties the certificate to
// it), the identity both were made with, and the pairing. Never the password, which isn't kept anywhere.
async fn place_in_app(data: &Path, udid: &str, fresh: bool) -> Result<(), Report> {
    let Ok(session) = std::fs::read(data.join(SESSION)) else { bail!("Not signed in") };
    let email = serde_json::from_slice::<Session>(&session)?.email;
    let pairing = pairing_file(data, udid, fresh).await?;
    let provider = device(udid).await?;
    let (_, Some(bundle)) = the_two_apps(&provider).await? else { bail!("The app isn't on the phone: install it first") };

    let store = storage(data);
    let mut files = vec![(SESSION.to_string(), session), ("pairing.plist".to_string(), pairing)];
    for key in ["anisette_state".to_string(), key_of(&email), signed_key(udid)] {
        if let Some(bytes) = store.retrieve_data(&key)? {
            files.push((format!("isideload/{key}"), bytes));
        }
    }
    // Until when it's signed now, in seconds: after a renewal by cable the app has no other way to know, since what's inside it says
    // only when it was installed. Not worth failing for.
    if let Ok(mut profiles) = MisagentClient::connect(&provider).await
        && let Ok(all) = profiles.copy_all().await
        && let Some(expires) = until(&all, &bundle)
    {
        files.push(("expires".to_string(), expires.to_string().into_bytes()));
    }
    let mut afc = HouseArrestClient::connect(&provider).await?.vend_container(bundle).await?;
    for (name, bytes) in files {
        let path = format!("{APP_KEYS}/{name}");
        // (its folders, from the top: one that's there already is fine)
        for (end, _) in path.match_indices('/').skip(1) {
            let _ = afc.mk_dir(&path[..end]).await;
        }
        let mut file = afc.open(path, AfcFopenMode::Wr).await?;
        file.write_entire(&bytes).await?;
        file.close().await?;
    }
    emit(json!({ "event": "done" }));
    Ok(())
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

#[derive(Deserialize)]
struct Item {
    from: PathBuf,
    to: String,
}

// Whether it's there already, from a sending that stopped halfway. Only what's in a folder (a song's file, named after the song's
// id): the files beside them say what there is, and that changes.
fn arrived(there: &HashMap<String, u64>, to: &str, size: u64) -> bool {
    to.contains('/') && there.get(to) == Some(&size)
}

// What the computer's app has, into the one on the phone: the files of the list ([{"from", "to"}]), in order. What the app does with
// them is its own.
async fn send(udid: &str, list: &Path) -> Result<(), Report> {
    let list: Vec<Item> = serde_json::from_slice(&std::fs::read(list)?)?;
    let Some(last) = list.last() else { bail!("Nothing to send") };
    let provider = trusted(udid).await?;
    let apps = InstallationProxyClient::connect(&provider).await?.get_apps(Some("User"), None).await?;
    let bundle = apps.keys().find(|id| id.starts_with(&format!("{APP}."))).cloned();
    let (Some(bundle), Some(1..)) = (bundle, ours_key(&apps, "UMPImport")) else {
        bail!("The app on the phone can't take it: install its latest version first");
    };

    stage("Sending", 0.0);
    let mut afc = HouseArrestClient::connect(&provider).await?.vend_container(bundle).await?;
    // Not all there any more, from here until the end. (It's fine if it wasn't.)
    let _ = afc.remove(format!("{INBOX}/{}", last.to)).await;

    let mut there = HashMap::new();
    let mut folders: Vec<&str> = list.iter().filter_map(|item| Some(item.to.rsplit_once('/')?.0)).collect();
    folders.sort();
    folders.dedup();
    afc.mk_dir(INBOX).await?;
    for folder in folders {
        let dir = format!("{INBOX}/{folder}");
        afc.mk_dir(dir.clone()).await?;
        for name in afc.list_dir(dir.clone()).await?.into_iter().filter(|n| n != "." && n != "..") {
            let (at, path) = (format!("{folder}/{name}"), format!("{dir}/{name}"));
            if list.iter().any(|item| item.to == at) {
                there.insert(at, afc.get_file_info(path).await?.size as u64);
            } else {
                afc.remove(path).await?;
            }
        }
    }

    // (a song's file that's gone from the computer meanwhile isn't a reason to stop)
    let mut left = Vec::new();
    for item in &list {
        match std::fs::metadata(&item.from) {
            Ok(file) if !arrived(&there, &item.to, file.len()) => left.push((item, file.len())),
            Ok(_) => {}
            Err(e) if item.to.contains('/') => eprintln!("Not sent, {}: {e}", item.to),
            Err(e) => bail!("{}: {e}", item.to),
        }
    }
    let total = left.iter().map(|(_, size)| size).sum::<u64>();
    let free = afc.get_device_info().await?.free_bytes as u64;
    if total + SPARE > free {
        bail!("Not enough space on the phone: {total} bytes to send, {free} free");
    }

    let mut sent = 0;
    let mut piece = vec![0; 1024 * 1024];
    for (item, _) in &left {
        let mut from = match File::open(&item.from) {
            Ok(from) => from,
            Err(e) if item.to.contains('/') => {
                eprintln!("Not sent, {}: {e}", item.to);
                continue;
            }
            Err(e) => bail!("{}: {e}", item.to),
        };
        let mut to = afc.open(format!("{INBOX}/{}", item.to), AfcFopenMode::Wr).await?;
        loop {
            let n = from.read(&mut piece)?;
            if n == 0 {
                break;
            }
            to.write_entire(&piece[..n]).await?;
            sent += n as u64;
            stage("Sending", (sent as f64 / total.max(1) as f64).min(1.0));
        }
        to.close().await?;
    }
    emit(json!({ "event": "done", "files": left.len(), "bytes": sent }));
    Ok(())
}

fn below(version: &str, major: u32, minor: u32) -> bool {
    let mut parts = version.split('.').map(|p| p.parse::<u32>().unwrap_or(0));
    (parts.next().unwrap_or(0), parts.next().unwrap_or(0)) < (major, minor)
}

#[cfg(test)]
mod tests {
    use super::{arrived, below};
    use std::collections::HashMap;

    #[test]
    fn sent_again() {
        let there = HashMap::from([("music/a.mp3".to_string(), 5), ("data.zip".to_string(), 5)]);
        assert!(arrived(&there, "music/a.mp3", 5));
        assert!(!arrived(&there, "music/a.mp3", 6));
        assert!(!arrived(&there, "music/b.mp3", 5));
        assert!(!arrived(&there, "data.zip", 5));
    }

    #[test]
    fn versions() {
        assert!(below("17.3.1", 17, 4));
        assert!(!below("17.4", 17, 4));
        assert!(!below("26.1", 17, 4));
        assert!(below("9.10", 17, 4));
    }
}
