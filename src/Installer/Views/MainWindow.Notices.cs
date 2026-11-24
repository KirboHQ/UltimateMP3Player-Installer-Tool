using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Installer;

// What a notice is about: something that's needed, something that isn't any more, a decision, news, something to do on the iPhone.
// Each has its own colour, icon and label, the same wherever it's shown.
public enum NoticeKind { Need, Gone, Ask, News, Phone }

// Yes without a button: nothing to press, it closes by itself when what it asks for is done. Act: what Yes does when it isn't the
// end of it (opening the Store, say: the notice stays until the app is there). Later: it can be put off (Esc, or a click beside it).
// Risky: Yes takes something away, so it isn't the button that stands out, nor the one Enter presses.
public sealed record Notice(NoticeKind Kind, string Title, string Text, string? Yes = null, string? No = null, string? Wait = null, string? Command = null, Action? Act = null, bool Later = false, bool Risky = false);

// The notices: one box over everything for whatever can't go unnoticed, and "what it needs".
public partial class MainWindow
{
    // Yes true, No false, put off null.
    private TaskCompletionSource<bool?>? _noticeAnswer;
    private Notice? _notice;
    private readonly HashSet<string> _noticesShown = new();

    private static (string Label, string Icon, IBrush Brush) Look(NoticeKind kind) => kind switch
    {
        NoticeKind.Need => (L.T("NEEDED"), "IconDownload", Icons.Brush("Warn")),
        NoticeKind.Gone => (L.T("NOT NEEDED ANYMORE"), "IconBin", Icons.Brush("Good")),
        NoticeKind.Ask => (L.T("YOUR DECISION"), "IconQuestion", Icons.Brush("Link")),
        NoticeKind.News => (L.T("NEW"), "IconSpark", Icons.Brush("Link")),
        _ => (L.T("ON YOUR IPHONE"), "IconPhone", Icons.Brush("Warn")),
    };

    private static IBrush Tint(IBrush brush, byte alpha) =>
        brush is ISolidColorBrush s ? new ImmutableSolidColorBrush(Color.FromArgb(alpha, s.Color.R, s.Color.G, s.Color.B)) : brush;

    private void SetUpNotices()
    {
        NoticeYes.Click += (_, _) =>
        {
            if (_notice?.Act is { } act) act();
            else Answer(true);
        };
        NoticeNo.Click += (_, _) => Answer(false);
        NoticeDim.PointerPressed += (_, _) => { if (_notice is { Later: true }) Answer(null); };
        NoticeCopy.Click += async (_, _) => { if (Clipboard is { } c) await c.SetTextAsync(_notice?.Command); };
        AutomationProperties.SetLiveSetting(NoticeTitle, AutomationLiveSetting.Assertive);
    }

    // Esc: put off what can be, No to what can't.
    private void NoticeEscape()
    {
        if (_notice is { Later: true }) Answer(null);
        else if (_notice is { No: not null }) Answer(false);
    }

    private void Answer(bool? answer)
    {
        var waiting = _noticeAnswer;
        CloseNotice();
        waiting?.TrySetResult(answer);
    }

    private async Task<bool?> AskAsync(Notice notice)
    {
        ShowNotice(notice);
        return await _noticeAnswer!.Task.WaitAsync(Ct);
    }

    private void ShowNotice(Notice notice)
    {
        // (one at a time: the newest, and whoever waited for the one before has no answer from it)
        _noticeAnswer?.TrySetResult(null);
        _noticeAnswer = new TaskCompletionSource<bool?>();
        bool shown = NoticeLayer.IsVisible && _notice != null;
        _notice = notice;

        var (label, icon, brush) = Look(notice.Kind);
        NoticeEdge.Background = brush;
        NoticeBadge.Background = Tint(brush, 0x2E);
        var drawn = Icons.Make(icon, brush, 26);
        drawn.HorizontalAlignment = HorizontalAlignment.Center;
        drawn.VerticalAlignment = VerticalAlignment.Center;
        NoticeBadge.Child = drawn;
        NoticeLabel.Text = label;
        NoticeLabel.Foreground = brush;
        NoticeTitle.Text = notice.Title;
        NoticeText.Text = notice.Text;
        NoticeText.IsVisible = notice.Text.Length > 0;
        NoticeCommand.IsVisible = notice.Command != null;
        NoticeCommandText.Text = notice.Command;
        NoticeWait.IsVisible = notice.Wait != null;
        NoticeWaitText.Text = notice.Wait;
        NoticeYes.IsVisible = notice.Yes != null;
        NoticeYes.Content = notice.Yes;
        NoticeYes.Classes.Set("primary", !notice.Risky);
        NoticeYes.Classes.Set("soft", notice.Risky);
        NoticeNo.IsVisible = notice.No != null;
        NoticeNo.Content = notice.No;
        NoticeButtons.IsVisible = notice.Yes != null || notice.No != null;
        // Something to do on the iPhone leaves the drawn one in sight, since it's showing what to tap.
        NoticeDim.Background = notice.Kind != NoticeKind.Phone ? new SolidColorBrush(Color.Parse("#B8000000")) : new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.36, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.56, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#00000000"), 0), new GradientStop(Color.Parse("#B8000000"), 1) },
        };

        NoticeLayer.IsVisible = true;
        double from = shown ? NoticeLayer.Opacity : 0;
        Motion.Tween(NoticeLayer, 240, Motion.CubicOut, k => NoticeLayer.Opacity = Motion.Lerp(from, 1, k));
        Motion.Tween(NoticeBox, 520, Motion.Spring, k => Motion.Place(NoticeBox, 0, (1 - k) * 26, 0.96 + 0.04 * k));
        Motion.Tween(NoticeBadge, 560, Motion.Spring, k => Motion.Place(NoticeBadge, 0, 0, 0.5 + 0.5 * k), delay: 120);
        (NoticeYes.IsVisible && !notice.Risky ? NoticeYes : NoticeNo).Focus();
    }

    private void CloseNotice()
    {
        if (_notice == null) return;
        _notice = null;
        _noticeAnswer?.TrySetResult(null);
        Motion.Tween(NoticeLayer, 160, Motion.CubicIn, k => NoticeLayer.Opacity = 1 - k, done: () =>
        {
            if (_notice == null) NoticeLayer.IsVisible = false;
        });
    }

    // ---- What's pending on the home page: a card at its top for each, until it's answered, and each opens by itself once per run.

    private sealed record Pending(string Id, Notice Notice, Action<bool?> Answered, bool Card = true);

    // The most pressing first. What the phone knows better (Learn) waits for it, if it's plugged in.
    private IEnumerable<Pending> PendingNotices()
    {
        var record = _phone == null || _learned == _phone.Udid ? _homeRecord : null;
        if (record is { How: Memory.Renewal.MissingVpn })
            yield return new Pending("vpn", new Notice(NoticeKind.Need, L.T("LocalDevVPN is missing"),
                L.F("The app on this iPhone renews itself, but only with LocalDevVPN, and it isn't on the iPhone anymore. Without it, the app stops opening after {0}.", Day(record.Until)),
                L.T("Set it up"), L.T("Renew it by cable instead"), Later: true), yes =>
            {
                if (yes == true) StartFresh(Way.Itself);
                else if (yes == false) Keep(() => { record.Renews = false; record.KeepsCable = true; });
            });
        if (record is { CanRenew: true, KeepsCable: false, How: Memory.Renewal.Cable or Memory.Renewal.SideStore })
            yield return new Pending("self", record.SideStore
                ? new Notice(NoticeKind.Gone, L.T("You can do without SideStore"),
                    L.T("From this version on, the app renews and updates itself. The LocalDevVPN you already have is all it needs: SideStore can go."),
                    L.T("Let it renew itself"), L.T("Keep SideStore"), Later: true)
                : new Notice(NoticeKind.Gone, L.T("You can do without the cable"),
                    L.T("From this version on, the app renews and updates itself on the iPhone. All it needs is LocalDevVPN, a small free app: setting it up takes a couple of minutes, once."),
                    L.T("Let it renew itself"), L.T("Keep the cable"), Later: true), yes =>
            {
                if (yes == true) StartFresh(Way.Itself);
                else if (yes == false) Keep(() => record.KeepsCable = true);
            });
        if (record is { CanImport: true, Brought: false } && _library is { } library)
            yield return new Pending("bring", new Notice(NoticeKind.News, L.T("Your music can come from this computer"),
                L.F("Ultimate MP3 Player is on this computer too, with {0} songs. They can go to the iPhone by cable, with playlists, statistics and the look you gave it: nothing that's there is deleted.", library.Songs),
                L.T("Choose what to bring"), L.T("No, thanks"), Later: true), yes =>
            {
                if (yes == true) StartFresh(Way.Bring);
                else if (yes == false) Keep(() => record.Brought = true);
            });
        if (record is { HasSideStore: true, SideStore: false, LeftSideStore: true })
            yield return new Pending("sidestore", new Notice(NoticeKind.Gone, L.T("You can delete SideStore"),
                L.T(record.Renews
                    ? "The app renews itself now, so SideStore has nothing left to do for it. If no other app of yours needs it, delete it from the iPhone: hold its icon, then Remove App. LocalDevVPN stays: the app uses it."
                    : "The app is renewed by cable now, so SideStore has nothing left to do for it. If no other app of yours needs them, delete SideStore and LocalDevVPN from the iPhone: hold the icon, then Remove App."),
                L.T("I'll delete it"), L.T("Keep SideStore"), Later: true), yes =>
            {
                if (yes == false) Keep(() => record.LeftSideStore = false);
            });
        if (_memory.WslLeftover && OperatingSystem.IsWindows())
            yield return new Pending("wsl", new Notice(NoticeKind.Gone, L.T("You can turn off WSL"),
                L.T("The older version used it, and nothing else is in it now. If no other program needs it or the Virtual Machine Platform (some Android emulators do), I can turn both off: Windows asks your permission, and they go at the next restart."),
                L.T("Turn them off"), L.T("Keep them"), Later: true), async yes =>
            {
                if (yes == true) await RemoveWslAsync();
                else if (yes == false) Keep(() => _memory.WslLeftover = false);
            });
        if (_newerTool is { } newer)
            yield return new Pending("tool", _toolReady
                ? new Notice(NoticeKind.News, L.F("Version {0} of this program is ready", newer),
                    L.T("It's already downloaded and checked. It goes in when you close this window, or right now."),
                    L.T("Restart now"), L.T("When I close it"), Later: true)
                : new Notice(NoticeKind.News, L.F("A new version of this program is out: {0}", newer),
                    L.T("When Apple changes its rules, a new version is what makes it work again."),
                    L.T("Open its page"), L.T("Not now"), Later: true), yes => { if (yes == true) UpdateTool(); }, Card: false);
    }

    private static string Day(DateTime utc) => utc.ToLocalTime().ToString("dddd d MMMM", L.Culture);

    private void Keep(Action change)
    {
        change();
        _memory.Save();
        if (_page == HomePage) RefreshHome();
    }

    private void RefreshNotices()
    {
        var pending = PendingNotices().ToList();
        // (two at most, one with the hint about plugging in, so that the page always fits: the next comes up as these are answered)
        HomeNotices.Children.Clear();
        foreach (var p in pending.Where(p => p.Card).Take(_phone == null ? 1 : 2)) HomeNotices.Children.Add(NoticeCard(p));
        HomeNotices.IsVisible = HomeNotices.Children.Count > 0;

        if (Previewing || _chainRunning || _notice != null || Sheet.IsVisible) return;
        if (pending.FirstOrDefault(p => !_noticesShown.Contains(p.Id)) is { } first) OpenNotice(first);
    }

    private async void OpenNotice(Pending pending)
    {
        _noticesShown.Add(pending.Id);
        try { pending.Answered(await AskAsync(pending.Notice)); }
        catch (OperationCanceledException) { }
    }

    private Control NoticeCard(Pending pending)
    {
        var (label, icon, brush) = Look(pending.Notice.Kind);
        var words = new StackPanel { Spacing = 1 };
        words.Children.Add(new TextBlock { Text = label, Classes = { "eyebrow" }, FontSize = 11, Foreground = brush });
        words.Children.Add(new TextBlock { Text = pending.Notice.Title, Classes = { "title" }, FontSize = 15.5 });
        var chevron = Icons.Make("IconChevron", Icons.Brush("Faint"), 15);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var drawn = Icons.Make(icon, brush, 20);
        drawn.Margin = new Thickness(0, 0, 14, 0);
        drawn.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.AddRange(new Control[] { drawn, words, chevron });
        Grid.SetColumn(words, 1);
        Grid.SetColumn(chevron, 2);
        var card = new Button { Classes = { "choice" }, Padding = new Thickness(18, 11), Background = Tint(brush, 0x1C), Content = grid };
        card.Click += (_, _) => OpenNotice(pending);
        return card;
    }

    // ---- What it needs: everything, with whether it's there and whether it's still needed.

    private void ShowNeeds()
    {
        var record = _memory.Phones.Count > 0 ? _homeRecord : null;
        var how = record?.How ?? Memory.Renewal.Cable;
        bool itself = how is Memory.Renewal.Itself or Memory.Renewal.MissingVpn, sideStore = how == Memory.Renewal.SideStore;
        string ok = L.T("NEEDED, AND THERE"), missing = L.T("NEEDED, MISSING"), needed = L.T("NEEDED"), optional = L.T("OPTIONAL"), gone = L.T("NOT NEEDED ANYMORE"), no = L.T("NOT NEEDED");
        var rows = new List<Control>();
        void Section(string text) => rows.Add(new TextBlock { Text = L.T(text), Classes = { "eyebrow" }, Margin = new Thickness(0, rows.Count > 0 ? 8 : 0, 0, -6) });
        void Row(string icon, string title, string text, string badge)
        {
            var brush = badge == missing ? Icons.Brush("Warn") : badge == ok || badge == needed || badge == gone ? Icons.Brush("Good") : Icons.Brush("Faint");
            var drawn = Icons.Make(icon, badge == gone || badge == no ? Icons.Brush("Faint") : Icons.Brush("Link"), 20);
            drawn.Margin = new Thickness(0, 2, 16, 0);
            drawn.VerticalAlignment = VerticalAlignment.Top;
            var pill = new Badge { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
            pill.Set(badge, brush);
            Grid.SetColumn(pill, 1);
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { new TextBlock { Text = title, Classes = { "title" }, TextWrapping = TextWrapping.Wrap }, pill } };
            var words = new StackPanel { Spacing = 2, Children = { head, new TextBlock { Text = L.T(text), Classes = { "small" }, Foreground = Icons.Brush("Soft") } } };
            Grid.SetColumn(words, 1);
            rows.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { drawn, words } });
        }

        Section("ON THIS COMPUTER");
        if (OperatingSystem.IsWindows())
            Row("IconComputer", L.T("Apple Devices"), "Apple's free app: without it Windows can't talk to the iPhone. You never have to open it, it's started by itself when it's needed.",
                Setup.AppleDevicesInstalled() ? ok : missing);
        else if (OperatingSystem.IsLinux())
            Row("IconComputer", "usbmuxd", "The small service that talks to the iPhone, from your system's packages.", needed);
        Row("IconPhone", L.T("The cable"), itself ? "The app renews itself on the iPhone. The cable is only the way back, should it ever not manage to." : sideStore ? "SideStore renews it on the iPhone. The cable is only the way back, should it ever not manage to." : "Every 7 days, to renew the app from here.",
            itself || sideStore ? gone : needed);
        Row("IconLock", L.T("A free Apple ID"), "Apple signs the app with it, for your iPhone only.", _appleId != null ? ok : needed);
        if (_memory.WslLeftover && OperatingSystem.IsWindows())
            Row("IconBin", "WSL", "The version before 1.1.0 used it. You can turn it off from the home page.", gone);

        Section("ON YOUR IPHONE");
        Row("IconSliders", L.T("Developer Mode, and trust in your Apple ID"), "Turned on once, the first time. Leave them on: without them iOS doesn't open the app.", record != null ? ok : needed);
        Row("IconDownload", "LocalDevVPN", itself || sideStore ? "A free app from the App Store, kept connected: it's how the app is reached without the cable. Nothing goes out of the iPhone through it."
            : "Only to have the app renewed without the cable: a free app from the App Store.",
            itself ? record!.Vpn ? ok : missing : sideStore ? needed : optional);
        if (sideStore) Row("IconDownload", "SideStore", "It renews the app on the iPhone. Open it every few days.", ok);
        else if (record is { HasSideStore: true, LeftSideStore: true }) Row("IconBin", "SideStore", "It doesn't renew this app anymore. If no other app of yours needs it, delete it from the iPhone.", gone);
        else Row("IconDownload", "SideStore", "Another way to renew the app on the iPhone, for those who already use it.", no);

        ShowSheet(L.T("What it needs"), "", rows: rows);
    }
}
