namespace Installer;

// Every screen with made-up values, for tests/Shots: no phone, no account, no network.
public partial class MainWindow
{
    internal static bool Previewing { get; set; }

    internal void Preview(string name)
    {
        _phone = new Usbmux.Device(1, "test");
        _phoneName = "iPhone";
        _phoneOs = "26.1";
        _appleId = "name@example.com";
        _release = new IpaRelease("5.1.1", "", 0, null);
        Sheet.IsVisible = false;
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
                BlockerCard.IsVisible = false;
                _serviceRow.Done(L.T("Ready"));
                _engineRow.Working(L.T("Downloading the signing engine"), 0.62);
                _appRow.Waiting();
                break;
            case "blocker":
                Go(ComputerPage, 1, Blue);
                Phone.Show(Scene.Showcase);
                Phone.ShowShot(2);
                _ = SolveAsync(new BlockedException(Blocker.WslConsent, "ask"));
                _serviceRow.Done(L.T("Ready"));
                _engineRow.Working(L.T("Checking"));
                break;
            case "connect":
                ConnectHelp.IsVisible = true;
                Go(ConnectPage, 2, Cyan);
                Phone.Show(Scene.Lock);
                Phone.SetCable(CableState.Near);
                break;
            case "connected":
                ConnectTitle.Text = L.F("There it is: {0}", _phoneName);
                ConnectText.Text = L.F("iOS {0}", _phoneOs);
                ConnectHelp.IsVisible = false;
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
                var advice = Errors.For("Error: -80045");
                AccountError.Text = advice.Title + ". " + advice.Body;
                AccountError.IsVisible = true;
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
                PhoneRequestCard.IsVisible = ConfirmCard.IsVisible = false;
                InstallPercent.Text = "62%";
                InstallBar.Set(0.62);
                InstallStage.Text = L.T("Sending it to your iPhone");
                Go(InstallPage, 4, Pink);
                Phone.Show(Scene.Home);
                Phone.SetCable(CableState.In);
                Phone.Installing(0.62);
                break;
            case "install-trust":
                ConfirmCard.IsVisible = false;
                InstallPercent.Text = "8%";
                InstallBar.Set(0.08);
                InstallStage.Text = L.T("Getting your iPhone ready");
                PhoneRequestText.Text = L.T("Tap Trust on your iPhone, then enter its passcode");
                PhoneRequestCard.IsVisible = true;
                Go(InstallPage, 4, Pink);
                Phone.Guide(Guides.TrustComputer());
                Phone.SetCable(CableState.In);
                break;
            case "install-confirm":
                PhoneRequestCard.IsVisible = false;
                ConfirmCard.IsVisible = true;
                InstallPercent.Text = "24%";
                InstallBar.Set(0.24);
                InstallStage.Text = L.T("Apple is preparing the signature");
                Go(InstallPage, 4, Pink);
                Phone.Show(Scene.Home);
                Phone.SetCable(CableState.In);
                Phone.Installing(0.24);
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
                ShowDone(true, false);
                Phone.SetCable(CableState.In);
                Party.At(0.75);
                break;
            case "renewed":
                ShowDone(false, true);
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
            case "problem":
                ShowProblem("Error: 0xe8008024 profile is banned");
                break;
        }
    }
}
