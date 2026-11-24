namespace Installer;

// Every screen with made-up values, for tests/Shots: no phone, no account, no network.
public partial class MainWindow
{
    internal static bool Previewing { get; set; }

    private static ComputerLibrary MadeUpLibrary() => new()
    {
        Songs = 148, Playlists = 6, Files = new[] { new ComputerLibrary.Item("", "music/a.mp3", 1_240_000_000) },
    };

    internal void Preview(string name)
    {
        _phone = new Usbmux.Device(1, "test");
        _phoneName = "iPhone";
        _phoneOs = "26.1";
        _appleId = "name@example.com";
        _release = new IpaRelease("5.1.1", "", 0, null);
        _learned = "test";
        _way = Way.App;
        _library = null;
        _newerTool = null;
        ToolUpdate.IsVisible = false;
        InstallSub.Text = L.T("Keep the cable plugged in. This takes about a minute.");
        InstallStop.IsVisible = false;
        Sheet.IsVisible = false;
        CloseNotice();
        Party.IsVisible = false;
        Phone.SetCable(CableState.Away);
        Phone.LockHintShown(true);
        PhoneView.GuideFrame = 0;

        switch (name)
        {
            case "welcome":
                GoWelcome();
                Phone.ShowShot(0);
                break;
            case "why":
                GoWelcome();
                WhyButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                break;
            case "computer":
                Go(ComputerPage, 1, Blue);
                Phone.Show(Scene.Showcase);
                Phone.ShowShot(1);
                _serviceRow.Done(L.T("Ready"));
                _appRow.Working(L.F("Downloading version {0}", "5.1.1"), 0.62);
                break;
            case "blocker":
                Go(ComputerPage, 1, Blue);
                Phone.Show(Scene.Showcase);
                Phone.ShowShot(2);
                _serviceRow.Working(L.T("Checking"));
                _appRow.Waiting();
                ShowNotice(BlockerNotice(Blocker.AppleDevices));
                break;
            case "blocker-linux":
                Go(ComputerPage, 1, Blue);
                Phone.Show(Scene.Showcase);
                Phone.ShowShot(2);
                ShowNotice(BlockerNotice(Blocker.Usbmuxd));
                break;
            case "needs":
                GoWelcome();
                Phone.ShowShot(0);
                ShowNeeds();
                break;
            case "needs-itself":
                _memory.Phones["test"] = new Memory.PhoneRecord { Version = "5.1.1", InstalledUtc = DateTime.UtcNow.AddDays(-1), CanRenew = true, Renews = true, Vpn = true, HasSideStore = true, LeftSideStore = true };
                ShowHome();
                Phone.ShowShot(3);
                ShowNeeds();
                break;
            case "connect":
                ConnectHelp.IsVisible = ConnectBack.IsVisible = true;
                Go(ConnectPage, 2, Cyan);
                Phone.Show(Scene.Lock);
                Phone.SetCable(CableState.Near);
                break;
            case "connected":
                ConnectTitle.Text = L.F("There it is: {0}", _phoneName);
                ConnectText.Text = L.F("iOS {0}", _phoneOs);
                ConnectHelp.IsVisible = ConnectBack.IsVisible = false;
                Go(ConnectPage, 2, Cyan);
                Phone.Show(Scene.Lock);
                Phone.LockHintShown(false);
                Phone.SetCable(CableState.In);
                Phone.Say(L.T("Connected"));
                break;
            case "account":
                AccountError.IsVisible = false;
                Go(AccountPage, 3, Purple);
                Phone.Show(Scene.Lock);
                Phone.LockHintShown(false);
                Phone.SetCable(CableState.In);
                break;
            case "account-error":
                ShowAccountError(Errors.For("Error: -80045"));
                EmailBox.Text = "name@example.com";
                Go(AccountPage, 3, Purple);
                Phone.Show(Scene.Lock);
                Phone.LockHintShown(false);
                Phone.SetCable(CableState.In);
                break;
            case "code":
            case "code2":
                PhoneView.GuideFrame = name == "code" ? 0 : 1;
                Go(CodePage, 3, Purple);
                Phone.Guide(Guides.AppleCode());
                Phone.SetCable(CableState.In);
                CodeBox.Text = "123";
                break;
            case "install":
                InstallTitle.Text = L.T("Installing the app");
                InstallPercent.Text = "62%";
                InstallBar.Set(0.62);
                InstallStage.Text = L.T("Sending it to your iPhone");
                Go(InstallPage, 4, Pink);
                Phone.Show(Scene.Home);
                Phone.SetCable(CableState.In);
                Phone.Installing(0.62);
                break;
            case "install-trust":
                InstallPercent.Text = "8%";
                InstallBar.Set(0.08);
                InstallStage.Text = L.T("Getting your iPhone ready");
                Go(InstallPage, 4, Pink);
                Phone.SetCable(CableState.In);
                AskOnPhone(PhoneRequest.Trust);
                break;
            case "install-confirm":
                InstallPercent.Text = "24%";
                InstallBar.Set(0.24);
                InstallStage.Text = L.T("Apple is preparing the signature");
                Go(InstallPage, 4, Pink);
                Phone.Show(Scene.Home);
                Phone.SetCable(CableState.In);
                Phone.Installing(0.24);
                ShowNotice(CertificateNotice());
                break;
            case "moving":
                InstallTitle.Text = L.T("Updating the app");
                InstallPercent.Text = "100%";
                InstallBar.Set(1);
                InstallStage.Text = L.F("Moving your music to the new version: {0}%", 42);
                Go(InstallPage, 0, Pink);
                Phone.Show(Scene.Home);
                Phone.SetCable(CableState.In);
                Phone.Installing(1);
                break;
            case "install-sidestore":
                InstallTitle.Text = L.T("Installing SideStore");
                InstallPercent.Text = "81%";
                InstallBar.Set(0.81);
                InstallStage.Text = L.T("Signing the app with your Apple ID");
                Go(InstallPage, 4, Pink);
                Phone.Show(Scene.Home);
                Phone.SetCable(CableState.In);
                Phone.Installing(1);
                break;
            case "self":
            case "self2":
            case "self3":
            case "self4":
                PhoneView.GuideFrame = name == "self" ? 1 : 0;
                Go(SelfPage, 5, Amber);
                ShowSelf(name switch { "self2" => 0, "self3" => 1, "self4" => 2, _ => -1 });
                break;
            case "bring":
            case "bring-look":
                ShowBring(MadeUpLibrary(), name == "bring" ? 5 : 0);
                _songsRow.IsChecked = name == "bring";
                break;
            case "bringing":
                InstallTitle.Text = L.T("Bringing your things");
                InstallSub.Text = L.T("Keep the cable plugged in. How long it takes depends on how much there is.");
                InstallPercent.Text = "38%";
                InstallBar.Set(0.38);
                InstallStage.Text = L.T("Sending everything to your iPhone");
                InstallStop.IsVisible = true;
                Go(InstallPage, 5, Pink);
                Phone.OpenApp();
                Phone.SetCable(CableState.In);
                break;
            case "brought":
                ShowDone(false, true, new Memory.PhoneRecord { InstalledUtc = DateTime.UtcNow }, brought: true);
                Phone.SetCable(CableState.In);
                Party.At(1.7);
                break;
            case "home-bring":
            case "home-bring-notice":
                _library = MadeUpLibrary();
                _memory.Phones["test"] = new Memory.PhoneRecord { Version = "5.1.1", InstalledUtc = DateTime.UtcNow.AddDays(-3), CanRenew = true, CanImport = true };
                ShowHome();
                Phone.ShowShot(3);
                if (name == "home-bring-notice") ShowNotice(PendingNotices().First(p => p.Id == "bring").Notice);
                break;
            case "sidestore-stuck":
                _memory.Phones["test"] = new Memory.PhoneRecord { Version = "5.1.1", InstalledUtc = DateTime.UtcNow.AddDays(-1) };
                ShowHome();
                Phone.ShowShot(3);
                ShowNotice(SideStoreNotice(Releases.SideStoreVersion));
                break;
            case "sidestore":
            case "sidestore2":
            case "sidestore3":
                PhoneView.GuideFrame = name == "sidestore3" ? 1 : 0;
                Go(SideStorePage, 5, Amber);
                ShowHandOver(name switch { "sidestore2" => 1, "sidestore3" => 2, _ => 0 });
                break;
            case "activate":
            case "activate2":
            case "activate3":
            case "activate4":
                PhoneView.GuideFrame = name switch { "activate2" => 1, "activate3" => 2, "activate4" => 3, _ => 0 };
                Go(ActivatePage, 5, Amber);
                _modeCard.Set(true, false);
                _trustCard.Set(false, false);
                Phone.Guide(Guides.DeveloperMode());
                Phone.SetCable(CableState.In);
                break;
            case "trust":
            case "trust3":
                PhoneView.GuideFrame = name == "trust" ? 1 : 3;
                Go(ActivatePage, 5, Amber);
                _modeCard.Set(false, true);
                _trustCard.Set(true, false);
                ActivateNext.Content = L.T("The app opens: done!");
                Phone.Guide(Guides.TrustAccount(_appleId));
                Phone.SetCable(CableState.In);
                break;
            case "done":
                ShowDone(true, false, new Memory.PhoneRecord { InstalledUtc = DateTime.UtcNow });
                Phone.SetCable(CableState.In);
                Party.At(0.75);
                break;
            case "renewed":
                ShowDone(false, true, new Memory.PhoneRecord { InstalledUtc = DateTime.UtcNow });
                Phone.SetCable(CableState.In);
                Party.At(1.7);
                break;
            case "done-itself":
                ShowDone(true, false, new Memory.PhoneRecord { InstalledUtc = DateTime.UtcNow, Renews = true, CanRenew = true, Vpn = true }, setUp: true);
                Phone.SetCable(CableState.In);
                Party.At(0.75);
                break;
            case "done-sidestore":
                _way = Way.SideStore;
                ShowDone(false, false, new Memory.PhoneRecord { InstalledUtc = DateTime.UtcNow, SideStore = true }, setUp: true);
                Phone.SetCable(CableState.In);
                Party.At(0.75);
                break;
            case "renewed-sidestore":
                ShowDone(false, true, new Memory.PhoneRecord { InstalledUtc = DateTime.UtcNow, SideStore = true });
                Phone.SetCable(CableState.In);
                Party.At(1.7);
                break;
            case "home":
                _memory.Phones["test"] = new Memory.PhoneRecord { Version = "5.1.0", InstalledUtc = DateTime.UtcNow.AddDays(-5) };
                ShowHome();
                Phone.ShowShot(3);
                break;
            case "home-away":
                _phone = null;
                _phoneName = null;
                _memory.Phones["test"] = new Memory.PhoneRecord { Version = "5.1.1", InstalledUtc = DateTime.UtcNow.AddDays(-6.5) };
                ShowHome();
                Phone.ShowShot(2);
                break;
            case "home-wsl":
                _memory.Phones["test"] = new Memory.PhoneRecord { Version = "5.1.1", InstalledUtc = DateTime.UtcNow.AddDays(-1) };
                _memory.WslLeftover = true;
                ShowHome();
                Phone.ShowShot(3);
                ShowNotice(PendingNotices().First().Notice);
                break;
            case "home-itself":
                _memory.WslLeftover = false;
                _memory.Phones["test"] = new Memory.PhoneRecord { Version = "5.1.1", InstalledUtc = DateTime.UtcNow.AddDays(-9), ExpiresUtc = DateTime.UtcNow.AddDays(4.5), CanRenew = true, Renews = true, Vpn = true };
                ShowHome();
                Phone.ShowShot(3);
                break;
            case "home-left":
            case "home-left-notice":
                _memory.WslLeftover = true;
                _memory.Phones["test"] = new Memory.PhoneRecord { Version = "5.1.1", InstalledUtc = DateTime.UtcNow.AddDays(-9), ExpiresUtc = DateTime.UtcNow.AddDays(4.5), CanRenew = true, Renews = true, Vpn = true, HasSideStore = true, LeftSideStore = true };
                ShowHome();
                Phone.ShowShot(3);
                if (name == "home-left-notice") ShowNotice(PendingNotices().First().Notice);
                _memory.WslLeftover = false;
                break;
            case "home-vpn":
            case "home-vpn-notice":
                _memory.Phones["test"] = new Memory.PhoneRecord { Version = "5.1.1", InstalledUtc = DateTime.UtcNow.AddDays(-4), CanRenew = true, Renews = true };
                ShowHome();
                Phone.ShowShot(3);
                if (name == "home-vpn-notice") ShowNotice(PendingNotices().First().Notice);
                break;
            case "home-self":
            case "home-self-notice":
                _memory.Phones["test"] = new Memory.PhoneRecord { Version = "5.1.1", InstalledUtc = DateTime.UtcNow.AddDays(-3), CanRenew = true };
                ShowHome();
                Phone.ShowShot(3);
                if (name == "home-self-notice") ShowNotice(PendingNotices().First().Notice);
                break;
            case "home-tool":
            case "home-tool-notice":
                _memory.Phones["test"] = new Memory.PhoneRecord { Version = "5.1.1", InstalledUtc = DateTime.UtcNow.AddDays(-2) };
                ShowTool("10.20.30", true);
                ShowHome();
                Phone.ShowShot(3);
                if (name == "home-tool-notice") OpenNotice(PendingNotices().First(p => p.Id == "tool"));
                break;
            case "home-sidestore":
                _memory.Phones["test"] = new Memory.PhoneRecord { Version = "5.1.1", InstalledUtc = DateTime.UtcNow.AddDays(-12), ExpiresUtc = DateTime.UtcNow.AddDays(5.5), SideStore = true };
                _memory.WslLeftover = false;
                ShowHome();
                Phone.ShowShot(3);
                break;
            case "problem":
                ShowProblem("Error: 0xe8008024 profile is banned");
                break;
        }
    }
}
