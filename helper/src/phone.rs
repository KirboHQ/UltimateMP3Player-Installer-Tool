// On the phone itself: the app renews and updates itself through LocalDevVPN, a VPN that stays inside the phone and lets an app
// reach the services a computer reaches by cable (the phone answers at 10.7.0.1). What proves it's allowed is the pairing the
// installer put beside the sign-in (cable.rs, place_in_app). Built on the computer too, with the same feature: pointed at a
// phone's address it's a way to try it by hand.

use std::{
    net::{IpAddr, SocketAddr},
    path::{Path, PathBuf},
    time::Duration,
};

use idevice::{
    IdeviceError, IdeviceService, RsdService,
    heartbeat::HeartbeatClient,
    misagent::MisagentClient,
    pairing_file::PairingFile,
    provider::TcpProvider,
    remote_pairing::{RemotePairingClient, RpPairingFile, RpPairingSocket, connect_tls_psk_tunnel_native},
    rsd::RsdHandshake,
    tcp::{adapter::Adapter, handle::AdapterHandle},
};
use isideload::sideload::install::{install_app, install_app_rsd};
use rootcause::prelude::*;
use serde_json::json;
use tokio::{net::TcpStream, task::JoinHandle, time::timeout};

use crate::*;

// The daemons' own ports: remote pairing from iOS 17.4, lockdown before.
const REMOTE_PAIRING: u16 = 49152;
const LOCKDOWN: u16 = 62078;
const PAIRING: &str = "pairing.plist";

pub async fn run(data: &Path, command: &str, rest: &[String]) -> Result<(), Report> {
    match (command, rest) {
        ("check-here", [bundle, at]) => {
            let mut phone = Phone::reach(data, at).await?;
            let expires = until(&phone.misagent().await?.copy_all().await?, bundle);
            emit(json!({ "event": "app", "bundle": bundle, "expires": expires }));
            Ok(())
        }
        ("renew-here", [bundle, at]) => renew(data, bundle, at).await,
        ("update-here", [bundle, at, ipa]) => update(data, bundle, at, PathBuf::from(ipa)).await,
        _ => bail!("usage: --data <folder> check-here <bundle> <address[:port]> | renew-here <bundle> <address[:port]> | update-here <bundle> <address[:port]> <ipa>"),
    }
}

// As cable.rs's renew: "renewed": false means the app has to be installed again (update-here with the same version does).
async fn renew(data: &Path, bundle: &str, at: &str) -> Result<(), Report> {
    stage("Renewing", 0.05);
    let not_this_way = |why: &str| {
        emit(json!({ "event": "done", "renewed": false, "why": why }));
        Ok(())
    };
    // The phone first: with the VPN off it says so at once, and Apple isn't asked for nothing.
    let mut phone = Phone::reach(data, at).await?;
    stage("Renewing", 0.3);
    let profile = match retrying(async || new_profile(data, bundle, &phone.udid).await).await? {
        Ok(profile) => profile,
        Err(why) => return not_this_way(&why),
    };
    stage("Renewing", 0.8);
    // What's really on the phone is what counts, also if its answer got lost on the way.
    let put = phone.misagent().await?.install(profile.bytes).await;
    let expires = until(&phone.misagent().await?.copy_all().await?, bundle);
    if expires.is_none_or(|e| e < profile.expires) {
        return not_this_way(&put.err().map_or("the phone didn't keep the new profile".to_string(), |e| e.to_string()));
    }
    stage("Renewing", 1.0);
    emit(json!({ "event": "done", "renewed": true, "expires": expires }));
    Ok(())
}

// A new version, signed here and installed over the app that's running this: iOS closes it towards the end, so whatever has to
// be kept is saved before starting. ("bundle" is only to say which app this is about: isideload names it as it did before.)
async fn update(data: &Path, bundle: &str, at: &str, ipa: PathBuf) -> Result<(), Report> {
    stage("Preparing", 0.0);
    let mut phone = Phone::reach(data, at).await?;
    let (email, session) = developer_session(data).await?;
    let mut loader = sideloader(data, email.clone(), session);
    let team = loader.get_team().await?;
    if bundle != format!("{APP}.{}", team.team_id) {
        bail!("This app was installed with another Apple ID");
    }
    let (signed, _) = loader
        .sign_app(
            ipa,
            Some(team),
            false,
            Some(|p: f32| {
                stage("Signing", p as f64);
                std::future::ready(())
            }),
            None,
            Some(&phone.udid),
        )
        .await?;
    // Before the install, not after it: by then this program may be gone.
    remember_certificate(data, &email, &phone.udid, &signed);
    let progress = |p: u64| stage("Installing", p as f64 / 100.0);
    let installed = match &mut phone.link {
        Link::Tunnel { adapter, services, .. } => install_app_rsd(adapter, services, &signed, progress).await,
        Link::Lockdown { provider, .. } => install_app(provider, &signed, progress).await,
    };
    let _ = std::fs::remove_dir_all(&signed);
    installed?;
    emit(json!({ "event": "done" }));
    Ok(())
}

// ---- The way to the phone's services

struct Phone {
    udid: String,
    link: Link,
}

enum Link {
    // iOS 17.4 and later: a tunnel, whose other end lists its services. The pairing's own connection has to stay open meanwhile.
    Tunnel { adapter: AdapterHandle, services: RsdHandshake, _pairing: RemotePairingClient<RpPairingSocket<TcpStream>> },
    // Before: lockdown, as on Wi-Fi, which hangs up on who doesn't answer its heartbeat.
    Lockdown { provider: TcpProvider, heartbeat: JoinHandle<()> },
}

impl Drop for Link {
    fn drop(&mut self) {
        if let Link::Lockdown { heartbeat, .. } = self {
            heartbeat.abort();
        }
    }
}

impl Phone {
    async fn reach(data: &Path, at: &str) -> Result<Phone, Report> {
        let Ok(bytes) = std::fs::read(data.join(PAIRING)) else { bail!("Not paired: plug the iPhone in with the installer once") };
        let plist: plist::Dictionary = plist::from_bytes(&bytes)?;
        let Some(udid) = plist.get("UDID").and_then(|v| v.as_string()).map(str::to_string) else { bail!("Not paired: the pairing doesn't say for which iPhone") };
        let link = match RpPairingFile::from_bytes(&bytes) {
            Ok(file) if plist.contains_key("private_key") => tunnel(file, address(at, REMOTE_PAIRING)?).await?,
            _ => lockdown(PairingFile::from_bytes(&bytes)?, address(at, LOCKDOWN)?.ip()).await?,
        };
        Ok(Phone { udid, link })
    }

    async fn misagent(&mut self) -> Result<MisagentClient, Report> {
        Ok(match &mut self.link {
            Link::Tunnel { adapter, services, .. } => MisagentClient::connect_rsd(adapter, services).await?,
            Link::Lockdown { provider, .. } => MisagentClient::connect(provider).await?,
        })
    }
}

fn address(at: &str, port: u16) -> Result<SocketAddr, Report> {
    if let Ok(full) = at.parse::<SocketAddr>() {
        return Ok(full);
    }
    Ok(SocketAddr::new(at.parse::<IpAddr>()?, port))
}

// The words the app looks for when LocalDevVPN isn't connected.
async fn dial(at: SocketAddr) -> Result<TcpStream, Report> {
    match timeout(Duration::from_secs(4), TcpStream::connect(at)).await {
        Ok(Ok(stream)) => Ok(stream),
        Ok(Err(e)) => bail!("The iPhone can't be reached at {at}: {e}"),
        Err(_) => bail!("The iPhone can't be reached at {at}: no answer"),
    }
}

async fn tunnel(mut file: RpPairingFile, at: SocketAddr) -> Result<Link, Report> {
    let host = file.identifier().to_string();
    let mut pairing = RemotePairingClient::new(RpPairingSocket::new(dial(at).await?), &host);
    pairing.attempt_pair_verify().await?;
    // Only checked, never made here: making one asks for a code on the phone, which the installer does over the cable.
    if pairing.validate_pairing(&mut file).await.is_err() {
        bail!("Not paired: iOS forgot this pairing, plug the iPhone in with the installer once");
    }
    let port = pairing.create_tcp_listener().await?;
    let tunnel = connect_tls_psk_tunnel_native(dial(SocketAddr::new(at.ip(), port)).await?, pairing.encryption_key()).await?;
    let (ours, theirs): (IpAddr, IpAddr) = (tunnel.info.client_address.parse()?, tunnel.info.server_address.parse()?);
    let list = tunnel.info.server_rsd_port;
    // Its packets stay as small as it makes them (1220 bytes): filled up to the tunnel's own size (16000), as idevice does elsewhere,
    // the phone takes them and never acknowledges them (iOS 26.6.2: 8000 pass, 12000 don't), and a profile doesn't fit in one.
    let mut adapter = Adapter::new(Box::new(tunnel.into_inner()), ours, theirs).to_async_handle();
    let stream = adapter.connect(list).await?;
    let services = RsdHandshake::new(stream).await?;
    Ok(Link::Tunnel { adapter, services, _pairing: pairing })
}

async fn lockdown(pairing_file: PairingFile, addr: IpAddr) -> Result<Link, Report> {
    drop(dial(SocketAddr::new(addr, LOCKDOWN)).await?);
    let provider = TcpProvider { addr, scope_id: None, pairing_file, label: MACHINE.to_string() };
    let mut beat = HeartbeatClient::connect(&provider).await?;
    let heartbeat = tokio::spawn(async move {
        while let Ok(_) | Err(IdeviceError::Heartbeat(_)) = beat.get_marco(15).await {
            if beat.send_polo().await.is_err() {
                return;
            }
        }
    });
    Ok(Link::Lockdown { provider, heartbeat })
}
