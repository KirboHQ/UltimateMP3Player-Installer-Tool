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

    private readonly StatusRow _serviceRow, _engineRow, _appRow;
    private readonly ActivateCard _modeCard, _trustCard;

    private Control? _page;
    private Aurora _front, _back;
    private Usbmux.Device? _phone;
    private string? _phoneName, _phoneOs, _appleId, _ipa;
    private IpaRelease? _release;
    private TaskCompletionSource? _phoneArrived;
    private TaskCompletionSource<string?>? _code;
    private TaskCompletionSource<bool>? _confirm;
    private TaskCompletionSource? _blockerDone, _activated;
    private Action? _blockerAction, _retry, _otherAccount;
    private string _problemDetails = "";
    private int _activateStep;
    private bool _removeArmed, _chainRunning;

    public MainWindow()
    {
        InitializeComponent();
        _front = AuroraFront;
        _back = AuroraBack;
        foreach (var colors in new[] { Violet, Blue, Cyan, Purple, Pink, Amber, Green, Red }) Aurora.Prepare(colors);

        _serviceRow = new StatusRow(L.T("Connection to the iPhone"));
        _engineRow = new StatusRow(L.T("Signing engine"));
        _appRow = new StatusRow(L.T("The app"));
        ComputerRows.Children.AddRange(new Control[] { _serviceRow, _engineRow, _appRow });

        _modeCard = new ActivateCard(1, L.T("Turn on Developer Mode"), L.T("Settings › Privacy & Security › Developer Mode. Turn it on, restart, and confirm with Turn On."));
        _trustCard = new ActivateCard(2, L.T("Trust your Apple ID"), L.T("Settings › General › VPN & Device Management › your Apple ID › Trust."));
        ActivateCards.Children.AddRange(new Control[] { _modeCard, _trustCard });

        WelcomeTitle.Text = L.T("All your music,\nalways with you.");
        AddFeature(0, 0, "IconDownload", "Offline, anywhere");
        AddFeature(1, 0, "IconLines", "Synced lyrics");
        AddFeature(0, 1, "IconSliders", "Equalizer and crossfade");
        AddFeature(1, 1, "IconPeople", "Listen with friends");

        StartButton.Click += (_, _) => StartChain();
        WhyButton.Click += (_, _) => ShowSheet(L.T("Why isn't it on the App Store?"), L.T("An app that downloads music from almost any site wouldn't get into the App Store. But Apple lets anyone install apps on their own iPhone by signing them with their own Apple ID, and that's what happens here, for free.\n\nThe one catch: the signature lasts 7 days. When it's about to end, plug in the cable and press Renew. Your music and playlists stay where they are."));
        TrustButton.Click += (_, _) => ShowSheet(L.T("Can I trust this?"), L.T("This program is open source, so anyone can check what it does. Your password and code go straight to Apple. A login token stays in the private Linux on this computer, until you sign out here.\n\nApple also wants proof that the login comes from an Apple device: an outside service creates a random identity for that, and it never sees your email or password.\n\nIt uses Apple's private tools for developers, so if you'd rather stay extra safe, create a second free Apple ID just for this: it works the same.\n\nSource code and licenses: github.com/KirboHQ/UltimateMP3Player-Installer-Tool"));
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
        ShareButton.Click += async (_, _) => await ShareAsync();
        HomeButton.Click += (_, _) => StartChain();
        SignOutButton.Click += async (_, _) => await SignOutAsync();
        RemoveButton.Click += async (_, _) => await RemoveAsync();
        ProblemRetry.Click += (_, _) => _retry?.Invoke();
        ProblemOther.Click += (_, _) => _otherAccount?.Invoke();
        ProblemDetails.Click += (_, _) => ShowSheet(L.T("Details"), _problemDetails, copy: true);
        ProblemReport.Click += (_, _) => Setup.Open(Releases.IssuesLink);
        SheetClose.Click += (_, _) => HideSheet();
        SheetDim.PointerPressed += (_, _) => HideSheet();
        SheetCopy.Click += async (_, _) => { if (Clipboard is { } c) await c.SetTextAsync(_problemDetails); };
        ToolUpdate.Click += (_, _) => Setup.Open(Releases.ShareLink);
        KeyDown += (_, e) => { if (e.Key == Key.Escape && Sheet.IsVisible) HideSheet(); };

        Closing += (_, _) => _closing.Cancel();
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
        _appleId = _memory.AppleId;
        if (_memory.ResumeSetup)
        {
            _memory.ResumeSetup = false;
            _memory.Save();
            GoWelcome();
            StartChain();
        }
        else if (_memory.EngineReady && _memory.Phones.Count > 0) ShowHome();
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
                ToolUpdate.Content = L.F("A new version of this program is out: {0}", newer);
                ToolUpdate.IsVisible = true;
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
            () => Dispatcher.UIThread.Post(() => { if (_phone != null) Gone(); }),
            Ct));
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

    private async void StartChain()
    {
        // (a second click, while the first one's work hasn't changed the page yet)
        if (_chainRunning) return;
        _chainRunning = true;
        try
        {
            await PrepareComputerAsync();
            await ConnectPhoneAsync();
            await SignInAsync();
            await InstallAsync();
        }
        catch (OperationCanceledException) when (Ct.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            // The person said stop: back where they came from.
            if (_memory.EngineReady && _memory.Phones.Count > 0) ShowHome();
            else GoWelcome();
        }
        catch (XtoolException e) { ShowProblem(e.Details); }
        catch (Exception e) { ShowProblem(e.Message); }
        finally { _chainRunning = false; }
    }

    private async Task PrepareComputerAsync()
    {
        if (_memory.EngineReady)
        {
            // Checked for real: someone may have removed it. It takes WSL a moment to wake up, so what comes next is on the screen already.
            if (_phone != null) ShowInstalling();
            bool ready = false;
            try { ready = await Setup.EngineReadyAsync(Ct) && await Usbmux.RunningAsync(Ct); }
            catch (BlockedException) { }
            if (ready) return;
            _memory.EngineReady = false;
        }

        Go(ComputerPage, 1, Blue);
        Phone.Show(Scene.Showcase);
        Phone.SetCable(CableState.Away);
        foreach (var row in new[] { _serviceRow, _engineRow, _appRow }) row.Waiting();
        BlockerCard.IsVisible = false;

        // The phone's service.
        _serviceRow.Working(L.T("Checking"));
        await UntilDoneAsync(async () =>
        {
            if (OperatingSystem.IsWindows() && Setup.AppleDevicesInstalled() && !await Usbmux.RunningAsync(Ct)) _serviceRow.Working(L.T("Starting Apple Devices"));
            await Setup.EnsurePhoneServiceAsync(Ct);
        });
        _serviceRow.Done(L.T("Ready"));

        // The engine.
        _engineRow.Working(L.T("Checking"));
        await UntilDoneAsync(async () =>
        {
            if (await Setup.EngineReadyAsync(Ct)) return;
            if (!await Setup.WslPresentAsync(Ct) || !Setup.VmPlatformPresent()) throw new BlockedException(Blocker.WslConsent, "ask");
            await Setup.PrepareEngineAsync((p, text) => Dispatcher.UIThread.Post(() => _engineRow.Working(text, p)), Ct);
        });
        _engineRow.Done(L.T("Ready"));
        _memory.EngineReady = true;
        _memory.Save();

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
    // on another thing to solve (permission refused; WSL in, and now a restart), which comes round the same way.
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
                while (!Setup.AppleDevicesInstalled()) await Task.Delay(1500, Ct);
                return;
            case Blocker.Usbmuxd:
                Card("usbmuxd is missing", "It's the small service that talks to the iPhone. Install it with your system's packages, for example:\nsudo apt install usbmuxd\nI'll notice by myself and carry on.", null, null);
                while (!await Usbmux.RunningAsync(Ct)) await Task.Delay(1500, Ct);
                return;
            case Blocker.ZipTools:
                Card("zip and unzip are missing", "The signing engine needs them to open the app and to close it again once signed. Install them with your system's packages, for example:\nsudo apt install zip unzip\nI'll notice by myself and carry on.", null, null);
                while (!Setup.HasZipTools()) await Task.Delay(1500, Ct);
                return;
            case Blocker.WslConsent:
                bool refused = blocked.Message != "ask";
                Card(refused ? "Permission wasn't given" : "Windows needs a small component",
                     refused ? "Without it I can't sign the app on this computer. Press the button whenever you're ready." : "The signature is made in a private little Linux. Windows will ask your permission to turn on WSL, its official way to run one. It stays out of your way, and you can remove it whenever you like.",
                     "Allow it", () => done.TrySetResult());
                await done.Task.WaitAsync(Ct);
                BlockerCard.IsVisible = false;
                _engineRow.Working(L.T("Checking"));
                await Setup.InstallWslAsync(Ct);
                if (!await Setup.WslPresentAsync(Ct) || !Setup.VmPlatformPresent()) throw new BlockedException(Blocker.Reboot);
                return;
            case Blocker.Reboot:
                Card("Restart your computer", "Windows needs a restart to finish. Save what you're working on: afterwards I'll reopen by myself, right from here.", "Restart now", () =>
                {
                    _memory.ResumeSetup = true;
                    _memory.Save();
                    Setup.RestartComputerAndResume();
                });
                await Task.Delay(Timeout.Infinite, Ct);
                return;
            case Blocker.Virtualization:
                Card("Virtualization is turned off", "It's a setting of your computer, off on some models. Restart, open the BIOS (usually F2, F10 or Del right at startup), turn on \"Virtualization\", \"SVM\" or \"VT-x\", and open me again. It takes one switch, and it's safe.", "Try again", () => done.TrySetResult());
                await done.Task.WaitAsync(Ct);
                return;
            default:
                Card("This Windows is too old", "WSL needs Windows 10 version 2004 or later. Update Windows from Settings and open me again.", null, null);
                await Task.Delay(Timeout.Infinite, Ct);
                return;
        }
    }

    private async Task ConnectPhoneAsync()
    {
        if (_phone != null) return;
        ConnectTitle.Text = L.T("Plug in your iPhone");
        ConnectText.Text = L.T("Use the cable and unlock it. I'll recognize it by myself.");
        ConnectHelp.IsVisible = true;
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
        ConnectHelp.IsVisible = false;
        await Task.Delay(1300, Ct);
    }

    private TaskCompletionSource? _loginAsked;

    private async Task SignInAsync()
    {
        _appleId = await Xtool.AccountAsync(Ct);
        if (_appleId != null) return;

        Go(AccountPage, 3, Purple);
        Phone.Show(Scene.Lock);
        Phone.LockHintShown(false);
        Phone.SetCable(CableState.In);
        AccountError.IsVisible = false;
        LoginSpinner.IsVisible = false;
        LoginButton.IsEnabled = true;
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

            LoginButton.IsEnabled = false;
            LoginSpinner.IsVisible = true;
            AccountError.IsVisible = false;
            try
            {
                await Xtool.LoginAsync(email, password, AskCodeAsync, Ct);
                _appleId = email;
                _memory.AppleId = email;
                _memory.Save();
                PasswordBox.Text = "";
                return;
            }
            catch (XtoolException e)
            {
                var advice = Errors.For(e.Details);
                AccountError.Text = advice.Title + ". " + advice.Body;
                AccountError.IsVisible = true;
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
            LoginButton.IsEnabled = true;
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

        double shown = 0;
        bool declined = false;
        try
        {
            await Xtool.InstallAsync(phone.Udid, _ipa!,
                (text, overall) =>
                {
                    if (text != null) InstallStage.Text = text;
                    InstallBar.Set(overall);
                    Phone.Installing(overall);
                    double from = shown;
                    shown = overall;
                    Motion.Tween(InstallPercent, 420, Motion.CubicOut, k => InstallPercent.Text = $"{Motion.Lerp(from, overall, k) * 100:0}%");
                },
                request =>
                {
                    PhoneRequestCard.IsVisible = request != PhoneRequest.None;
                    PhoneRequestText.Text = L.T(request == PhoneRequest.Trust ? "Tap Trust on your iPhone, then enter its passcode" : "Unlock your iPhone");
                    if (request == PhoneRequest.Trust) Phone.Guide(Guides.TrustComputer());
                    else Phone.Show(request == PhoneRequest.Unlock ? Scene.Lock : Scene.Home);
                },
                async () =>
                {
                    _confirm = new TaskCompletionSource<bool>();
                    ConfirmCard.IsVisible = true;
                    bool yes = await _confirm.Task.WaitAsync(Ct);
                    ConfirmCard.IsVisible = false;
                    declined = !yes;
                    return yes;
                }, Ct);
        }
        catch (XtoolException) when (declined)
        {
            throw new OperationCanceledException();
        }

        InstallBar.Set(1);
        InstallPercent.Text = "100%";
        Phone.Installed();
        _memory.Phones[phone.Udid] = new Memory.PhoneRecord { Version = _release?.Version ?? "", InstalledUtc = DateTime.UtcNow };
        _memory.Save();
        await Task.Delay(900, Ct);

        if (first) await ActivateAsync();
        ShowDone(first, same);
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

    private void ShowDone(bool first, bool same)
    {
        DoneTitle.Text = L.T(first ? "You did it!" : same ? "Good for another 7 days!" : "It's updated!");
        DoneText.Text = L.T(first ? "Open Ultimate MP3 Player on your iPhone and download your first song." : "Everything's as you left it, and your music is all there.");
        DoneUntil.Text = L.F("Works until {0}", DateTime.Now.AddDays(Memory.SignedDays).ToString("dddd d MMMM", L.Culture));
        ShareText.Text = L.T("Copy the link for a friend");
        Go(DonePage, 0, Green);
        Phone.OpenApp();
        Party.Burst();
        Phone.Say(L.T("Ready"));
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
        HomeState.Text = record != null ? L.F("Version {0} on this iPhone", record.Version) : L.T("This iPhone doesn't have the app yet");
        HomeExpiry.IsVisible = record != null;
        if (record != null)
        {
            var left = record.InstalledUtc.AddDays(Memory.SignedDays) - DateTime.UtcNow;
            int days = (int)Math.Ceiling(left.TotalDays);
            HomeExpiry.Text = left <= TimeSpan.Zero ? L.T("Expired: renew it to open it again")
                : days <= 1 && left.TotalHours < 24 ? L.T(DateTime.UtcNow.Date == record.InstalledUtc.AddDays(Memory.SignedDays).Date ? "Expires today" : "Expires tomorrow")
                : L.F("Expires in {0} days", days);
            HomeExpiry.Foreground = Icons.Brush(days <= 1 ? "Bad" : days <= 2 ? "Warn" : "Good");
        }

        HomeAccount.Text = _appleId != null ? L.F("{0} is signed in", _appleId) : "";
        SignOutButton.IsVisible = _appleId != null;

        Phone.SetCable(_phone != null ? CableState.In : CableState.Near);
        HomeButton.IsEnabled = _phone != null;
        HomeButton.Content = _phone == null ? L.T("Plug in your iPhone to continue")
            : record == null ? L.T("Install on this iPhone")
            : _release != null && Releases.IsNewer(_release.Version, record.Version) ? L.F("Update to {0}", _release.Version)
            : L.T("Renew for 7 days");
    }

    private async Task SignOutAsync()
    {
        try { await Xtool.LogoutAsync(Ct); }
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
        _memory.EngineReady = false;
        _ipa = null;
        _appleId = null;
        RemoveButton.IsEnabled = true;
        GoWelcome();
        ShowSheet(L.T("Remove everything from this computer"), L.T("Everything is removed. The app stays on your iPhone until its signature ends."));
    }

    // ---- When something goes wrong

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
