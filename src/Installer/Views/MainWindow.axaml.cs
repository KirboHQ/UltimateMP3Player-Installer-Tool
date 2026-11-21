using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Installer;

public partial class MainWindow : Window
{
    private const int Steps = 5;

    // The colours of the light behind each step.
    private static readonly (string, string) Violet = ("#6D4DFF", "#FF4FA3"), Blue = ("#2F6BFF", "#6D4DFF"), Cyan = ("#12A5C9", "#6D4DFF"),
        Purple = ("#A54CF5", "#2F6BFF"), Pink = ("#FF4FA3", "#FF8A4C"), Amber = ("#E8891E", "#FF4FA3"), Green = ("#16B981", "#12A5C9"), Red = ("#C2410C", "#6D4DFF");

    private readonly Memory _memory = Memory.Load();
    private readonly CancellationTokenSource _closing = new();
    private CancellationToken Ct => _closing.Token;

    private readonly StatusRow _serviceRow, _appRow;
    private readonly ActivateCard _modeCard, _trustCard, _vpnCard, _signInCard, _sourceCard;

    private Control? _page;
    private Aurora _front, _back;
    private Usbmux.Device? _phone;
    private string? _phoneName, _phoneOs, _appleId, _ipa;
    private IpaRelease? _release;
    private TaskCompletionSource? _phoneArrived;
    private TaskCompletionSource<string?>? _code;
    private TaskCompletionSource<bool>? _confirm;
    private TaskCompletionSource<bool>? _chosen;
    private TaskCompletionSource? _blockerDone, _activated, _handedOver;
    private Action? _blockerAction, _retry, _otherAccount;
    private string _problemDetails = "";
    private int _activateStep, _handStep;

    // How this phone gets renewed, for this run: by SideStore on it, or from here by cable; null until asked (ChooseAsync).
    // "Try again" keeps it.
    private bool? _sideStore;
    // Home's main button: the app alone, by cable, on any phone that has it. A phone with SideStore keeps it: it's the same app
    // signed with the same certificate (the helper put it inside SideStore), so SideStore carries on renewing it.
    // (only for a phone that has the app: a new one goes the whole way, its choice included)
    private bool _appOnly;
    // The phone the home page shows: the one plugged in, or with none seen the last one done.
    private Memory.PhoneRecord? _homeRecord;
    private bool _removeArmed, _chainRunning, _toolReady;

    public MainWindow()
    {
        InitializeComponent();
        _front = AuroraFront;
        _back = AuroraBack;
        foreach (var colors in new[] { Violet, Blue, Cyan, Purple, Pink, Amber, Green, Red }) Aurora.Prepare(colors);

        _serviceRow = new StatusRow(L.T("Connection to the iPhone"));
        _appRow = new StatusRow(L.T("The app"));
        ComputerRows.Children.AddRange(new Control[] { _serviceRow, _appRow });

        _modeCard = new ActivateCard(1, L.T("Turn on Developer Mode"), L.T("Settings › Privacy & Security › Developer Mode. Turn it on, restart, and confirm with Turn On."));
        _trustCard = new ActivateCard(2, L.T("Trust your Apple ID"), L.T("Settings › General › VPN & Device Management › your Apple ID › Trust."));
        ActivateCards.Children.AddRange(new Control[] { _modeCard, _trustCard });

        _vpnCard = new ActivateCard(1, L.T("Get LocalDevVPN"), L.T("Free, from the App Store. Open it, tap Connect and allow the VPN: SideStore uses it to reach the iPhone."));
        // (it shows nothing while it gets ready the first time, which looks broken: hence the words about waiting)
        _signInCard = new ActivateCard(2, L.T("Open SideStore and sign in"), L.T("Settings › Sign in with Apple ID, the same one. The first time it takes a couple of minutes to open: wait, don't reinstall it."));
        _sourceCard = new ActivateCard(3, L.T("Add Ultimate MP3 Player"), L.T("Scan the code and tap FREE: SideStore installs it over this one, your music stays, and from then on it renews it."));
        SideStoreCards.Children.AddRange(new Control[] { _vpnCard, _signInCard, _sourceCard });

        WelcomeTitle.Text = L.T("All your music,\nalways with you.");
        AddFeature(0, 0, "IconDownload", "Offline, anywhere");
        AddFeature(1, 0, "IconLines", "Synced lyrics");
        AddFeature(0, 1, "IconSliders", "Equalizer and crossfade");
        AddFeature(1, 1, "IconPeople", "Listen with friends");

        StartButton.Click += (_, _) => StartFresh(null);
        WhyButton.Click += (_, _) => ShowSheet(L.T("Why isn't it on the App Store?"), L.T("An app that downloads music from almost any site wouldn't get into the App Store. But Apple lets anyone install apps on their own iPhone by signing them with their own Apple ID, and that's what happens here, for free.\n\nThe one catch: the signature lasts 7 days. When it's about to end, plug in the cable and press Renew. Your music and playlists stay where they are."));
        TrustButton.Click += (_, _) => ShowSheet(L.T("Can I trust this?"), L.T("This program is open source, so anyone can check what it does. Your password and code go straight to Apple. A login token stays on this computer, until you sign out here.\n\nApple also wants proof that the login comes from an Apple device: an outside service creates a random identity for that, and it never sees your email or password.\n\nIt uses Apple's private tools for developers, so if you'd rather stay extra safe, create a second free Apple ID just for this: it works the same.\n\nSource code and licenses: github.com/KirboHQ/UltimateMP3Player-Installer-Tool"));
        BlockerButton.Click += (_, _) => _blockerAction?.Invoke();
        LoginButton.Click += (_, _) => _loginAsked?.TrySetResult();
        EmailBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) PasswordBox.Focus(); };
        PasswordBox.KeyDown += (_, e) => { if (e.Key == Key.Enter && LoginButton.IsEnabled) _loginAsked?.TrySetResult(); };
        CodeBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) SubmitCode(); };
        CodeBox.TextChanged += (_, _) => { if (CodeBox.Text?.Length == 6 && CodeBox.Text.All(char.IsDigit)) SubmitCode(); };
        CodeButton.Click += (_, _) => SubmitCode();
        CodeBack.Click += (_, _) => _code?.TrySetResult(null);
        ConfirmYes.Click += (_, _) => _confirm?.TrySetResult(true);
        ConfirmNo.Click += (_, _) => _confirm?.TrySetResult(false);
        ActivateNext.Click += (_, _) => NextActivate();
        ActivateSkip.Click += (_, _) => _activated?.TrySetResult();
        ChooseCable.Click += (_, _) => _chosen?.TrySetResult(false);
        ChooseSideStore.Click += (_, _) => _chosen?.TrySetResult(true);
        SideStoreNext.Click += (_, _) => NextHandOver();
        ShareButton.Click += async (_, _) => await ShareAsync();
        HomeButton.Click += (_, _) => StartFresh(null, appOnly: true);
        HomeRepair.Click += (_, _) => StartFresh(null);
        // (to SideStore, or away from it)
        HomeSideStore.Click += (_, _) => StartFresh(_homeRecord?.SideStore != true);
        SignOutButton.Click += async (_, _) => await SignOutAsync();
        RemoveButton.Click += async (_, _) => await RemoveAsync();
        WslRemove.Click += async (_, _) => await RemoveWslAsync();
        WslKeep.Click += (_, _) =>
        {
            _memory.WslLeftover = false;
            _memory.Save();
            WslCard.IsVisible = false;
        };
        ProblemRetry.Click += (_, _) => _retry?.Invoke();
        foreach (var back in new[] { BlockerBack, ConnectBack, ChoiceBack, AccountBack, SideStoreBack }) back.Click += (_, _) => GoBack();
        ProblemBack.Click += (_, _) => LeaveToStart();
        DoneButton.Click += (_, _) => LeaveToStart();
        ProblemOther.Click += (_, _) => _otherAccount?.Invoke();
        ProblemDetails.Click += (_, _) => ShowSheet(L.T("Details"), _problemDetails, copy: true);
        ProblemReport.Click += (_, _) => Setup.Open(Releases.IssuesLink);
        AccountStatus.Click += (_, _) => Setup.Open(Releases.AppleStatusLink);
        ProblemStatus.Click += (_, _) => Setup.Open(Releases.AppleStatusLink);
        SheetClose.Click += (_, _) => HideSheet();
        SheetDim.PointerPressed += (_, _) => HideSheet();
        SheetCopy.Click += async (_, _) => { if (Clipboard is { } c) await c.SetTextAsync(_problemDetails); };
        ToolUpdate.Click += (_, _) =>
        {
            if (!_toolReady) Setup.Open(Releases.ShareLink);
            else if (!_chainRunning)
            {
                SelfUpdate.Apply(relaunch: true);
                Close();
            }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && Sheet.IsVisible) HideSheet(); };

        // The light behind everything would take the clicks meant for the title bar: this strip moves the window instead, and a
        // double click maximizes it, as a title bar does. It leaves the system's buttons free (on the right, on a Mac on the left).
        DragStrip.Margin = OperatingSystem.IsMacOS() ? new Thickness(80, 0, 0, 0) : new Thickness(0, 0, 150, 0);
        DragStrip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else BeginMoveDrag(e);
        };

        Closing += (_, _) =>
        {
            _closing.Cancel();
            if (Previewing) return;
            // (a newer version downloaded meanwhile goes in now, for the next time)
            SelfUpdate.Apply(relaunch: false);
            Setup.StopAppleDevicesIfStarted();
        };
        Activated += (_, _) => Phone.Awake = true;
        Deactivated += (_, _) => Phone.Awake = false;
        Opened += (_, _) => { if (!Previewing) Begin(); };
    }

    private void AddFeature(int column, int row, string icon, string text)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 8, 10) };
        line.Children.Add(Icons.Make(icon, Icons.Brush("Link"), 17));
        line.Children.Add(new TextBlock { Text = L.T(text), FontSize = 15, Foreground = Icons.Brush("Ink"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(line, column);
        Grid.SetRow(line, row);
        Features.Children.Add(line);
    }

    // ---- From the first moment

    private void Begin()
    {
        _ = WatchPhonesAsync();
        _ = LookForNewsAsync();
        _ = TidyUpAsync();
        _ = Task.Run(SelfUpdate.CleanUp);
        _appleId = _memory.AppleId;
        if (_memory.Phones.Count > 0) ShowHome();
        else GoWelcome();
    }

    // Once, after updating from a version that used xtool: what it put on this computer goes (Setup.RemoveOldEngineAsync).
    private async Task TidyUpAsync()
    {
        if (_memory.OldEngineRemoved) return;
        try
        {
            bool wslEmptied = await Task.Run(() => Setup.RemoveOldEngineAsync(Ct));
            _memory.OldEngineRemoved = true;
            _memory.WslLeftover = wslEmptied;
            _memory.Save();
            if (_page == HomePage) RefreshHome();
        }
        // (whatever went wrong, it's tried again at the next start)
        catch { }
    }

    // "Back" on a page that waits for the person: the wait ends cancelled, and the chain stops as it does when they say stop
    // (StartChain), on Home or, with no phone done yet, the welcome page. Waits already over are left as they are.
    private void GoBack()
    {
        foreach (var wait in new[] { _blockerDone, _phoneArrived, _loginAsked, _handedOver }) wait?.TrySetCanceled();
        _chosen?.TrySetCanceled();
    }

    // From a page the chain has already left: the end, or a problem.
    private void LeaveToStart()
    {
        if (_memory.Phones.Count > 0) ShowHome();
        else GoWelcome();
    }

    private void GoWelcome()
    {
        Go(WelcomePage, 0, Violet);
        Phone.Show(Scene.Showcase);
        Phone.SetCable(CableState.Away);
        StartButton.Focus();
    }

    private async Task LookForNewsAsync()
    {
        try
        {
            var release = Releases.LatestIpaAsync(Ct);
            var tool = Releases.NewerToolAsync(Ct);
            _release = await release;
            if (_page == HomePage) RefreshHome();
            if (await tool is { } newer)
            {
                // Downloaded in the background and put in place by itself where it can; elsewhere a link to the release.
                _toolReady = await SelfUpdate.PrepareAsync(newer, Ct);
                ToolUpdate.Content = _toolReady
                    ? L.F("Version {0} is ready: it goes in when you close this window. Click here to restart now.", newer.Version)
                    : L.F("A new version of this program is out: {0}", newer.Version);
                // (not in the middle of an install, where restarting would cut it short)
                ToolUpdate.IsVisible = !_chainRunning;
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WatchPhonesAsync()
    {
        void Gone()
        {
            _phone = null;
            _phoneName = _phoneOs = null;
            if (_page == HomePage) RefreshHome();
            if (_page == ConnectPage) Phone.SetCable(CableState.Near);
        }
        await Task.Run(() => Usbmux.WatchAsync(
            device => Dispatcher.UIThread.Post(() => _ = PhoneCameAsync(device)),
            id => Dispatcher.UIThread.Post(() => { if (_phone?.Id == id) Gone(); }),
            () => Dispatcher.UIThread.Post(() => { if (_phone != null) Gone(); WakeAppleDevices(); }),
            Ct));
    }

    // On Windows Apple's service runs only with Apple Devices (or from logon, if that's left on): without it a phone plugged in
    // goes unseen, and the home page would wait for nothing. So there it's started by itself, once, as the install steps do.
    private bool _wokeAppleDevices;

    private async void WakeAppleDevices()
    {
        if (!OperatingSystem.IsWindows() || _wokeAppleDevices || _page != HomePage || _chainRunning || !Setup.AppleDevicesInstalled()) return;
        _wokeAppleDevices = true;
        bool front = IsActive;
        try
        {
            await Setup.StartAppleDevicesAsync(Ct);
            for (int i = 0; i < 40 && !await Usbmux.RunningAsync(Ct); i++) await Task.Delay(500, Ct);
        }
        catch (OperationCanceledException) { return; }
        // (if it came to opening Apple Devices, its window is over this one)
        if (front && !IsActive) Activate();
    }

    // shortcut: with two iPhones on the computer, the last one plugged in is the one; a chooser if anyone ever asks.
    private async Task PhoneCameAsync(Usbmux.Device device)
    {
        try
        {
            var (name, version) = await Usbmux.InfoAsync(device.Id, Ct);
            _phone = device;
            _phoneName = name ?? "iPhone";
            _phoneOs = version;
            _phoneArrived?.TrySetResult();
            if (_page == HomePage) RefreshHome();
        }
        catch (OperationCanceledException) { }
    }

    // ---- The way through: each step skips itself when it isn't needed, so "try again" and coming back are the same path.

    private void StartFresh(bool? sideStore, bool appOnly = false)
    {
        if (_chainRunning) return;
        _sideStore = sideStore;
        _appOnly = appOnly;
        StartChain();
    }

    private async void StartChain()
    {
        // (a second click, while the first one's work hasn't changed the page yet)
        if (_chainRunning) return;
        _chainRunning = true;
        ToolUpdate.IsVisible = false;
        try
        {
            await PrepareComputerAsync();
            await ConnectPhoneAsync();
            _sideStore ??= await ChooseAsync();
            await SignInAsync();
            await InstallAsync();
        }
        catch (OperationCanceledException) when (Ct.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            // The person said stop: back where they came from.
            if (_memory.Phones.Count > 0) ShowHome();
            else GoWelcome();
        }
        catch (HelperException e) { ShowProblem(e.Details); }
        catch (Exception e) { ShowProblem(e.Message); }
        finally
        {
            _chainRunning = false;
            ToolUpdate.IsVisible = ToolUpdate.Content != null;
        }
    }

    private async Task PrepareComputerAsync()
    {
        // Coming back: Apple's service usually answers already, and then there's nothing to get ready (the helper comes with this program).
        if (_memory.Phones.Count > 0 && await Usbmux.RunningAsync(Ct)) return;

        Go(ComputerPage, 1, Blue);
        Phone.Show(Scene.Showcase);
        Phone.SetCable(CableState.Away);
        foreach (var row in new[] { _serviceRow, _appRow }) row.Waiting();
        BlockerCard.IsVisible = false;

        // The phone's service.
        _serviceRow.Working(L.T("Checking"));
        await UntilDoneAsync(async () =>
        {
            bool starting = OperatingSystem.IsWindows() && Setup.AppleDevicesInstalled() && !await Usbmux.RunningAsync(Ct), front = IsActive;
            if (starting) _serviceRow.Working(L.T("Starting Apple Devices"));
            await Setup.EnsurePhoneServiceAsync(Ct);
            // (if it came to opening Apple Devices, its window is over this one, where the next steps are)
            if (starting && front && !IsActive) Activate();
        });
        _serviceRow.Done(L.T("Ready"));

        // The app itself, now that it's all going, so that later steps have nothing left to wait for.
        await FetchAppAsync(_appRow);
        await Task.Delay(450, Ct);
    }

    private async Task FetchAppAsync(StatusRow? row)
    {
        _release ??= await Releases.LatestIpaAsync(Ct);
        if (_release == null) throw new NoInternetException();
        var release = _release;
        row?.Working(L.F("Downloading version {0}", release.Version), 0);
        _ipa = await Releases.FetchIpaAsync(release, p => Dispatcher.UIThread.Post(() =>
        {
            row?.Working(L.F("Downloading version {0}", release.Version), p);
            if (row == null) InstallStage.Text = L.F("Downloading version {0}", release.Version);
        }), Ct);
        row?.Done(L.F("Version {0}", release.Version));
    }

    // Does a piece of the setup, and when it needs the person, shows them what and tries again when that's solved. Solving can end
    // on another thing to solve, which comes round the same way.
    private async Task UntilDoneAsync(Func<Task> work)
    {
        BlockedException? blocked = null;
        while (true)
        {
            try
            {
                if (blocked is { } now)
                {
                    blocked = null;
                    await SolveAsync(now);
                }
                await work();
                BlockerCard.IsVisible = false;
                return;
            }
            catch (BlockedException next)
            {
                blocked = next;
            }
        }
    }

    private async Task SolveAsync(BlockedException blocked)
    {
        _blockerDone = new TaskCompletionSource();
        var done = _blockerDone;
        // A look every 1.5 s, and at once when the person goes back.
        async Task Wait()
        {
            await Task.WhenAny(Task.Delay(1500, Ct), done.Task);
            Ct.ThrowIfCancellationRequested();
            if (done.Task.IsCanceled) throw new OperationCanceledException();
        }
        void Card(string title, string text, string? button, Action? action)
        {
            BlockerTitle.Text = L.T(title);
            BlockerText.Text = L.T(text);
            BlockerButton.IsVisible = button != null;
            if (button != null) BlockerButton.Content = L.T(button);
            _blockerAction = action;
            if (!BlockerCard.IsVisible)
            {
                BlockerCard.IsVisible = true;
                BlockerCard.Opacity = 0;
                Motion.Tween(BlockerCard, 340, Motion.CubicOut, k =>
                {
                    BlockerCard.Opacity = k;
                    Motion.Place(BlockerCard, 0, (1 - k) * 12);
                });
            }
        }

        switch (blocked.Blocker)
        {
            case Blocker.AppleDevices:
                Card("Install Apple Devices", "It's Apple's free app that lets Windows talk to the iPhone. Press Get in the Store: I'll notice by myself and carry on.", "Open the Store", Setup.OpenAppleDevicesInStore);
                while (!Setup.AppleDevicesInstalled()) await Wait();
                return;
            case Blocker.Usbmuxd:
                Card("usbmuxd is missing", "It's the small service that talks to the iPhone. Install it with your system's packages, for example:\nsudo apt install usbmuxd\nI'll notice by myself and carry on.", null, null);
                while (!await Usbmux.RunningAsync(Ct)) await Wait();
                return;
            default:
                return;
        }
    }

    private async Task ConnectPhoneAsync()
    {
        if (_phone != null) return;
        ConnectTitle.Text = L.T("Plug in your iPhone");
        ConnectText.Text = L.T("Use the cable and unlock it. I'll recognize it by myself.");
        ConnectHelp.IsVisible = ConnectBack.IsVisible = true;
        Go(ConnectPage, 2, Cyan);
        Phone.Show(Scene.Lock);
        Phone.LockHintShown(true);
        Phone.SetCable(CableState.Near);
        _phoneArrived = new TaskCompletionSource();
        await _phoneArrived.Task.WaitAsync(Ct);

        Phone.LockHintShown(false);
        Phone.SetCable(CableState.In);
        Phone.Say(L.T("Connected"));
        ConnectTitle.Text = L.F("There it is: {0}", _phoneName ?? "iPhone");
        ConnectText.Text = _phoneOs != null ? L.F("iOS {0}", _phoneOs) : "";
        ConnectHelp.IsVisible = ConnectBack.IsVisible = false;
        await Task.Delay(1300, Ct);
    }

    // The first time on a phone: renewed from here by cable, or by SideStore on the phone itself. Later, the way it was set up
    // (the Home page has its own button to move to SideStore).
    private async Task<bool> ChooseAsync()
    {
        if (_phone != null && _memory.Phones.TryGetValue(_phone.Udid, out var record)) return record.SideStore;
        _chosen = new TaskCompletionSource<bool>();
        Go(ChoicePage, 2, Cyan);
        Phone.Show(Scene.Home);
        Phone.SetCable(CableState.In);
        ChooseCable.Focus();
        return await _chosen.Task.WaitAsync(Ct);
    }

    private TaskCompletionSource? _loginAsked;

    private async Task SignInAsync()
    {
        _appleId = await Helper.AccountAsync(Ct);
        if (_appleId != null) return;

        Go(AccountPage, 3, Purple);
        Phone.Show(Scene.Lock);
        Phone.LockHintShown(false);
        Phone.SetCable(CableState.In);
        AccountError.IsVisible = AccountStatus.IsVisible = false;
        LoginSpinner.IsVisible = false;
        LoginButton.IsEnabled = AccountBack.IsEnabled = true;
        EmailBox.Text ??= _memory.AppleId;
        (string.IsNullOrEmpty(EmailBox.Text) ? EmailBox : PasswordBox).Focus();

        while (true)
        {
            _loginAsked = new TaskCompletionSource();
            await _loginAsked.Task.WaitAsync(Ct);
            string email = (EmailBox.Text ?? "").Trim(), password = PasswordBox.Text ?? "";
            if (email.Length == 0 || password.Length == 0)
            {
                (email.Length == 0 ? EmailBox : PasswordBox).Focus();
                continue;
            }

            LoginButton.IsEnabled = AccountBack.IsEnabled = false;
            LoginSpinner.IsVisible = true;
            AccountError.IsVisible = AccountStatus.IsVisible = false;
            try
            {
                await Helper.LoginAsync(email, password, AskCodeAsync, Ct);
                _appleId = email;
                _memory.AppleId = email;
                _memory.Save();
                PasswordBox.Text = "";
                return;
            }
            catch (HelperException e)
            {
                ShowAccountError(Errors.For(e.Details));
                _problemDetails = e.Details;
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                // Went back from the code.
            }
            CodeSpinner.IsVisible = false;
            if (_page != AccountPage) Go(AccountPage, 3, Purple);
            Phone.Show(Scene.Lock);
            PasswordBox.Text = "";
            PasswordBox.Focus();
            LoginButton.IsEnabled = AccountBack.IsEnabled = true;
            LoginSpinner.IsVisible = false;
        }
    }

    private async Task<string?> AskCodeAsync()
    {
        CodeBox.Text = "";
        CodeSpinner.IsVisible = false;
        CodeButton.IsEnabled = true;
        Go(CodePage, 3, Purple);
        Phone.Guide(Guides.AppleCode());
        CodeBox.Focus();
        _code = new TaskCompletionSource<string?>();
        string? code = await _code.Task.WaitAsync(Ct);
        CodeButton.IsEnabled = false;
        CodeSpinner.IsVisible = true;
        return code;
    }

    private void SubmitCode()
    {
        if (CodeButton.IsEnabled && CodeBox.Text is { Length: > 0 } code) _code?.TrySetResult(code);
    }

    private (bool First, bool Same) ShowInstalling()
    {
        Memory.PhoneRecord? record = null;
        bool first = _phone == null || !_memory.Phones.TryGetValue(_phone.Udid, out record);
        bool same = !first && _release != null && record!.Version == _release.Version;
        InstallTitle.Text = L.T(first ? "Installing the app" : same ? "Renewing the app" : "Updating the app");
        InstallPercent.Text = "0%";
        InstallBar.Reset();
        InstallStage.Text = L.T("Getting the app ready");
        PhoneRequestCard.IsVisible = ConfirmCard.IsVisible = false;
        Go(InstallPage, first ? 4 : 0, Pink);
        Phone.Show(Scene.Home);
        Phone.SetCable(CableState.In);
        Phone.Installing(0);
        return (first, same);
    }

    private async Task InstallAsync()
    {
        var phone = _phone;
        if (phone == null)
        {
            await ConnectPhoneAsync();
            phone = _phone!;
        }
        var (first, same) = ShowInstalling();

        // Also when a newer version came out since it was fetched, or when this is a later run.
        if (_ipa == null || _release != null && !_ipa.Contains(_release.Version)) await FetchAppAsync(null);

        void Ask(PhoneRequest request)
        {
            PhoneRequestCard.IsVisible = request != PhoneRequest.None;
            PhoneRequestText.Text = L.T(request == PhoneRequest.Trust ? "Tap Trust on your iPhone, then enter its passcode" : "Unlock your iPhone");
            if (request == PhoneRequest.Trust) Phone.Guide(Guides.TrustComputer());
            else Phone.Show(request == PhoneRequest.Unlock ? Scene.Lock : Scene.Home);
        }

        // With SideStore the bar is the app's up to 60%, then SideStore's.
        _memory.Phones.TryGetValue(phone.Udid, out var before);
        bool appOnly = _appOnly && before != null, sideStore = _sideStore == true && !appOnly;
        double appPart = sideStore ? 0.6 : 1, shown = 0;
        void Show(string? text, double overall, bool app)
        {
            if (text != null) InstallStage.Text = text;
            InstallBar.Set(overall);
            if (app) Phone.Installing(overall / appPart);
            double from = shown;
            shown = overall;
            Motion.Tween(InstallPercent, 420, Motion.CubicOut, k => InstallPercent.Text = $"{Motion.Lerp(from, overall, k) * 100:0}%");
        }

        bool declined = false;
        async Task<bool> Confirm()
        {
            _confirm = new TaskCompletionSource<bool>();
            ConfirmCard.IsVisible = true;
            bool yes = await _confirm.Task.WaitAsync(Ct);
            ConfirmCard.IsVisible = false;
            declined = !yes;
            return yes;
        }

        bool existed = false;
        try
        {
            await Helper.InstallAsync(phone.Udid, _ipa!, (text, overall) => Show(text, overall * appPart, true), Ask, Confirm, Ct);
            Show(null, appPart, true);
            // A phone last done with xtool now has the app twice: the old copy gives its music to this one, then goes. Done before
            // the new one is ever opened, so there's nothing of its own to lose.
            if (before?.ByHelper != true)
                await Helper.MoveFromOldAppAsync(phone.Udid, p => InstallStage.Text = L.F("Moving your music to the new version: {0}%", (int)(p * 100)), Ask, Ct);
            Phone.Installed();
            // Kept before SideStore's part, which can fail on its own: the app is renewed either way. Not the first time on a phone:
            // trying again then has to go through the first time's steps (Developer Mode, trusting the Apple ID) once more.
            if (before != null)
            {
                _memory.Phones[phone.Udid] = new Memory.PhoneRecord { Version = _release?.Version ?? "", InstalledUtc = DateTime.UtcNow, ByHelper = true, SideStore = before.SideStore };
                _memory.Save();
            }

            if (sideStore)
            {
                InstallTitle.Text = L.T(before?.SideStore == true ? "Renewing SideStore" : "Installing SideStore");
                InstallStage.Text = L.T("Downloading SideStore");
                string store = await Releases.FetchSideStoreAsync(_ => { }, Ct);
                // Set up before: its pairing is made anew, which is the repair when SideStore can't reach the phone any more.
                existed = await Helper.InstallSideStoreAsync(phone.Udid, store, before?.SideStore == true,
                    (text, overall) => Show(text, appPart + overall * (1 - appPart), false), Ask, Confirm, Ct);
            }
        }
        catch (HelperException) when (declined)
        {
            throw new OperationCanceledException();
        }

        InstallBar.Set(1);
        InstallPercent.Text = "100%";
        // SideStore only once the person has done their part on the phone: until then, this phone is still renewed by cable. That
        // part starts over when SideStore had been deleted (its sign-in and its list of apps went with it), and a run by cable
        // takes the phone back to the cable, unless it was the app alone from Home.
        var record = new Memory.PhoneRecord { Version = _release?.Version ?? "", InstalledUtc = DateTime.UtcNow, ByHelper = true, SideStore = (sideStore && existed || appOnly) && before?.SideStore == true };
        _memory.Phones[phone.Udid] = record;
        _memory.Save();
        await Task.Delay(900, Ct);

        if (first) await ActivateAsync();
        bool handedOver = sideStore && !record.SideStore;
        if (handedOver)
        {
            await HandOverAsync();
            record.SideStore = true;
            _memory.Save();
        }
        ShowDone(first, same, sideStore, handedOver, alongSideStore: !sideStore && record.SideStore);
    }

    private async Task ActivateAsync()
    {
        _activateStep = 0;
        _activated = new TaskCompletionSource();
        Go(ActivatePage, 5, Amber);
        ActivateNext.Content = L.T("Done, next");
        _modeCard.Set(active: true, done: false);
        _trustCard.Set(active: false, done: false);
        Phone.Guide(Guides.DeveloperMode());
        await _activated.Task.WaitAsync(Ct);
    }

    private void NextActivate()
    {
        if (_activateStep == 0)
        {
            _activateStep = 1;
            _modeCard.Set(active: false, done: true);
            _trustCard.Set(active: true, done: false);
            ActivateNext.Content = L.T("The app opens: done!");
            Phone.Guide(Guides.TrustAccount(_appleId ?? "Apple ID"));
            return;
        }
        _activated?.TrySetResult();
    }

    // SideStore's part on the phone, done once: LocalDevVPN, its sign-in, the app's source. Only the person can do them, so each
    // waits for them, with the phone beside showing how and a code for the camera where there's a link to open.
    private async Task HandOverAsync()
    {
        _handedOver = new TaskCompletionSource();
        Go(SideStorePage, 5, Amber);
        ShowHandOver(0);
        await _handedOver.Task.WaitAsync(Ct);
    }

    private void ShowHandOver(int step)
    {
        _handStep = step;
        var cards = new[] { _vpnCard, _signInCard, _sourceCard };
        for (int i = 0; i < cards.Length; i++) cards[i].Set(active: i == step, done: i < step);
        string? link = step switch { 0 => Releases.LocalDevVpnLink, 2 => Releases.SideStoreSourceLink, _ => null };
        SideStoreQr.IsVisible = SideStoreScan.IsVisible = link != null;
        if (link != null) SideStoreQr.Show(link);
        SideStoreNext.Content = L.T(step < 2 ? "Done, next" : "Installed from SideStore: done!");
        Phone.Guide(step switch { 0 => Guides.LocalDevVpn(), 1 => Guides.SideStoreSignIn(_appleId ?? "Apple ID"), _ => Guides.SideStoreSource() });
        Phone.SetCable(CableState.In);
    }

    private void NextHandOver()
    {
        if (_handStep < 2) ShowHandOver(_handStep + 1);
        else _handedOver?.TrySetResult();
    }

    // handedOver: SideStore was just set up, rather than renewed or repaired. alongSideStore: the app alone by cable on a phone
    // that SideStore renews too.
    private void ShowDone(bool first, bool same, bool sideStore = false, bool handedOver = false, bool alongSideStore = false)
    {
        DoneTitle.Text = L.T(first ? "You did it!" : handedOver ? "It renews by itself now!" : same ? "Good for another 7 days!" : "It's updated!");
        DoneText.Text = L.T(first ? "Open Ultimate MP3 Player on your iPhone and download your first song." : "Everything's as you left it, and your music is all there.");
        DoneUntil.Text = sideStore ? L.T("From now on SideStore renews it") : L.F("Works until {0}", DateTime.Now.AddDays(Memory.SignedDays).ToString("dddd d MMMM", L.Culture));
        DoneHint.Text = L.T(sideStore ? "Open SideStore every few days, with LocalDevVPN connected. If it ever stops, come back here and press Repair SideStore."
            : alongSideStore ? "SideStore keeps renewing it by itself. If it ever can't, come back here before then."
            : "Before then, come back here: one click and it's good for 7 more days.");
        ShareText.Text = L.T("Copy the link for a friend");
        Go(DonePage, 0, Green);
        Phone.OpenApp();
        Party.Burst();
        Phone.Say(L.T("Ready"));
        DoneButton.Focus();
    }

    private async Task ShareAsync()
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(Releases.ShareLink);
        ShareText.Text = L.T("Copied! Now paste it into a chat");
    }

    // ---- Coming back

    private void ShowHome()
    {
        Go(HomePage, 0, Violet);
        Phone.Show(Scene.Showcase);
        _removeArmed = false;
        RemoveButton.Content = L.T("Remove everything from this computer");
        RefreshHome();
        HomeButton.Focus();
    }

    private void RefreshHome()
    {
        Memory.PhoneRecord? record = null;
        if (_phone != null) _memory.Phones.TryGetValue(_phone.Udid, out record);
        record ??= _phone == null ? _memory.Phones.Values.OrderByDescending(r => r.InstalledUtc).FirstOrDefault() : null;

        HomePhone.Text = _phoneName ?? "iPhone";
        HomeState.Text = record == null ? L.T("This iPhone doesn't have the app yet")
            // (SideStore updates it on the phone: the version known here may be behind)
            : record.SideStore ? L.T("Ultimate MP3 Player and SideStore on this iPhone")
            : L.F("Version {0} on this iPhone", record.Version);
        HomeExpiry.IsVisible = record != null;
        if (record is { SideStore: true })
        {
            HomeExpiry.Text = L.T("SideStore renews it by itself");
            HomeExpiry.Foreground = Icons.Brush("Good");
        }
        else if (record != null)
        {
            var left = record.InstalledUtc.AddDays(Memory.SignedDays) - DateTime.UtcNow;
            int days = (int)Math.Ceiling(left.TotalDays);
            HomeExpiry.Text = left <= TimeSpan.Zero ? L.T("Expired: renew it to open it again")
                : days <= 1 && left.TotalHours < 24 ? L.T(DateTime.UtcNow.Date == record.InstalledUtc.AddDays(Memory.SignedDays).Date ? "Expires today" : "Expires tomorrow")
                : L.F("Expires in {0} days", days);
            HomeExpiry.Foreground = Icons.Brush(days <= 1 ? "Bad" : days <= 2 ? "Warn" : "Good");
        }

        HomeAccount.Text = _appleId != null ? L.F("{0} is signed in", _appleId) : "";
        WslCard.IsVisible = _memory.WslLeftover && OperatingSystem.IsWindows();
        SignOutButton.IsVisible = _appleId != null;

        Phone.SetCable(_phone != null ? CableState.In : CableState.Near);
        // (a phone plugged in while Apple's service was off can go unseen until it's plugged in again)
        HomeHint.IsVisible = _phone == null;
        // The app by cable, on every phone that has it, SideStore or not (with SideStore the version known here may be behind the
        // phone's: "update" then just renews it). SideStore's own repair, which also makes its pairing anew, is the link below.
        // All of them also with no phone seen: the way through gets the computer ready (Apple Devices included) and asks for it.
        _homeRecord = record;
        HomeButton.Content = record == null ? L.T("Install on this iPhone")
            : _release != null && Releases.IsNewer(_release.Version, record.Version) ? L.F("Update to {0}", _release.Version)
            : L.T("Renew for 7 days");
        HomeRepair.IsVisible = record?.SideStore == true;
        HomeSideStore.IsVisible = record != null;
        HomeSideStore.Content = L.T(record?.SideStore == true ? "Stop renewing it with SideStore" : "Let the iPhone renew it by itself, with SideStore");
    }

    private async Task SignOutAsync()
    {
        try { await Helper.LogoutAsync(Ct); }
        catch (Exception e) when (e is not OperationCanceledException) { }
        _appleId = null;
        _memory.AppleId = null;
        _memory.Save();
        RefreshHome();
    }

    private async Task RemoveAsync()
    {
        if (!_removeArmed)
        {
            _removeArmed = true;
            RemoveButton.Content = L.T("Press again to confirm");
            return;
        }
        RemoveButton.IsEnabled = false;
        await Setup.RemoveEverythingAsync(Ct);
        _memory.Phones.Clear();
        _memory.AppleId = null;
        _memory.WslLeftover = false;
        _ipa = null;
        _appleId = null;
        RemoveButton.IsEnabled = true;
        GoWelcome();
        ShowSheet(L.T("Remove everything from this computer"), L.T("Everything is removed. The app stays on your iPhone until its signature ends."));
    }

    // WSL and the Virtual Machine Platform off, when the old version left WSL empty and the person says so (Setup.RemoveWslAsync).
    private async Task RemoveWslAsync()
    {
        WslRemove.IsEnabled = WslKeep.IsEnabled = false;
        try
        {
            if (await Setup.RemoveWslAsync(Ct))
            {
                _memory.WslLeftover = false;
                _memory.Save();
                WslCard.IsVisible = false;
                ShowSheet(L.T("WSL isn't needed anymore"), L.T("Done: WSL and the Virtual Machine Platform go away at the next restart."));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        finally { WslRemove.IsEnabled = WslKeep.IsEnabled = true; }
    }

    // ---- When something goes wrong

    private void ShowAccountError(Errors.Advice advice)
    {
        AccountError.Text = advice.Title + ". " + advice.Body;
        AccountError.IsVisible = true;
        AccountStatus.IsVisible = advice.AppleSide;
    }

    private void ShowProblem(string details)
    {
        _problemDetails = details;
        var advice = details == NoInternetException.Text
            ? new Errors.Advice(L.T("No internet connection"), L.T("I can't reach GitHub to get the app. Check your connection and try again."))
            : Errors.For(details);
        ProblemTitle.Text = advice.Title;
        ProblemText.Text = advice.Body;
        ProblemOther.IsVisible = advice.Other != null;
        ProblemOther.Content = advice.Other;
        ProblemStatus.IsVisible = advice.AppleSide;
        _retry = StartChain;
        _otherAccount = async () =>
        {
            await SignOutAsync();
            StartChain();
        };
        Go(ProblemPage, 0, Red);
        Phone.Show(Scene.Lock);
        Phone.LockHintShown(false);
        ProblemRetry.Focus();
    }

    private sealed class NoInternetException() : Exception(Text)
    {
        public const string Text = "no release found";
    }

    // ---- Going from page to page

    private void Go(Control page, int step, (string, string) colors)
    {
        Paint(colors);
        SetStep(step);
        if (ReferenceEquals(page, _page)) return;
        var old = _page;
        _page = page;

        if (old != null)
        {
            Motion.Tween(old, 170, Motion.CubicIn, k =>
            {
                old.Opacity = 1 - k;
                Motion.Place(old, -22 * k);
            }, done: () =>
            {
                if (!ReferenceEquals(_page, old)) old.IsVisible = false;
            });
        }

        // The new one comes in a line at a time.
        page.IsVisible = true;
        page.Opacity = 1;
        Motion.Place(page);
        Motion.Stop(page);
        if (page is not StackPanel lines) return;
        int i = 0;
        foreach (var line in lines.Children)
        {
            if (!line.IsVisible) continue;
            double delay = (old != null ? 90 : 40) + 55 * i++;
            line.Opacity = Motion.Instant ? 1 : 0;
            var c = line;
            Motion.Tween(c, 520, Motion.QuintOut, k =>
            {
                c.Opacity = k;
                Motion.Place(c, 28 * (1 - k));
            }, delay);
        }
    }

    private void SetStep(int step)
    {
        bool shown = step > 0;
        if (shown)
        {
            StepLabel.Text = L.F("STEP {0} OF {1}", step, Steps);
            StepBar.Set((double)step / Steps);
        }
        double from = Header.Opacity, to = shown ? 1 : 0;
        if (from != to) Motion.Tween(Header, 300, Motion.CubicOut, k => Header.Opacity = Motion.Lerp(from, to, k));
    }

    // The light: the new colours fade in over the old ones, on the whole window for 800 ms, then it's a still picture again.
    private (string, string)? _painted;

    private void Paint((string, string) colors)
    {
        if (_painted == colors) return;
        _painted = colors;
        (_front, _back) = (_back, _front);
        _front.Paint(colors);
        // The one painted now goes on top.
        _front.ZIndex = 1;
        _back.ZIndex = 0;
        _front.Opacity = 0;
        var fading = _front;
        Motion.Tween(fading, 800, Motion.InOut, k => fading.Opacity = k);
    }

    // ---- The sheet

    private void ShowSheet(string title, string text, bool copy = false)
    {
        SheetTitle.Text = title;
        SheetText.Text = text;
        SheetCopy.IsVisible = copy;
        Sheet.IsVisible = true;
        Motion.Tween(Sheet, 280, Motion.CubicOut, k =>
        {
            Sheet.Opacity = k;
            Motion.Place(SheetBox, 0, (1 - k) * 18);
        });
        SheetClose.Focus();
    }

    private void HideSheet() => Motion.Tween(Sheet, 160, Motion.CubicIn, k => Sheet.Opacity = 1 - k, done: () => Sheet.IsVisible = false);
}

// One thing being got ready: a spinner, then a tick.
public sealed class StatusRow : Grid
{
    private readonly Spinner _spinner = new();
    private readonly Border _tick;
    private readonly Border _dot;
    private readonly TextBlock _detail;
    private readonly Bar _bar = new() { Height = 4, Margin = new Thickness(0, 7, 0, 2), IsVisible = false };
    private string _state = "";

    public StatusRow(string title)
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*");
        var slot = new Panel { Width = 28, Height = 28, Margin = new Thickness(0, 1, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        _dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Icons.Brush("Line"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _tick = new Border { CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.Parse("#263DDC97")), IsVisible = false, Child = Icons.Make("IconCheck", Icons.Brush("Good"), 14) };
        _spinner.HorizontalAlignment = HorizontalAlignment.Center;
        _spinner.VerticalAlignment = VerticalAlignment.Center;
        _spinner.IsVisible = false;
        slot.Children.AddRange(new Control[] { _dot, _spinner, _tick });
        Children.Add(slot);

        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock { Text = title, Classes = { "title" } });
        _detail = new TextBlock { Classes = { "small" } };
        text.Children.Add(_detail);
        text.Children.Add(_bar);
        SetColumn(text, 1);
        Children.Add(text);
        Waiting();
    }

    public void Waiting()
    {
        _state = "waiting";
        _dot.IsVisible = true;
        _spinner.IsVisible = _tick.IsVisible = _bar.IsVisible = false;
        _bar.Reset();
        _detail.Text = L.T("Waiting");
    }

    // Progress: 0..1 with a bar; null: it's busy, no idea how long.
    public void Working(string detail, double? progress = null)
    {
        _state = "working";
        _dot.IsVisible = _tick.IsVisible = false;
        _spinner.IsVisible = true;
        _detail.Text = detail;
        _bar.IsVisible = true;
        _bar.Set(progress ?? -1);
    }

    public void Done(string detail)
    {
        if (_state == "done") return;
        _state = "done";
        _dot.IsVisible = _spinner.IsVisible = _bar.IsVisible = false;
        _bar.Reset();
        _detail.Text = detail;
        _tick.IsVisible = true;
        Motion.Tween(_tick, 480, Motion.Spring, k => Motion.Place(_tick, 0, 0, k));
    }
}

// One of the two last things to do on the phone.
public sealed class ActivateCard : Border
{
    private readonly Border _badge;
    private readonly TextBlock _number;
    private readonly Control _tick;
    private readonly TextBlock _text;

    public ActivateCard(int number, string title, string text)
    {
        Classes.Add("card");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        _number = new TextBlock { Text = number.ToString(), FontWeight = FontWeight.Bold, FontSize = 14, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _tick = Icons.Make("IconCheck", Brushes.White, 14);
        _tick.IsVisible = false;
        _badge = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top, Child = new Panel { Children = { _number, _tick } } };
        grid.Children.Add(_badge);
        var words = new StackPanel { Spacing = 3 };
        words.Children.Add(new TextBlock { Text = title, Classes = { "title" } });
        _text = new TextBlock { Text = text, Classes = { "small" }, Foreground = Icons.Brush("Soft") };
        words.Children.Add(_text);
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        Child = grid;
    }

    public void Set(bool active, bool done)
    {
        _number.IsVisible = !done;
        _tick.IsVisible = done;
        _badge.Background = done ? Icons.Brush("Good") : active ? Icons.Brush("Brand") : Icons.Brush("Line");
        double from = Opacity, to = active || done ? 1 : 0.5;
        Motion.Tween(this, 260, Motion.CubicOut, k => Opacity = Motion.Lerp(from, to, k));
    }
}
