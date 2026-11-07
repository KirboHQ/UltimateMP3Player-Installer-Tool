using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Installer;

public enum Scene { Showcase, Lock, Home, Guide, App }
public enum CableState { Away, Near, In }

public sealed partial class PhoneView : UserControl
{
    private const string Assets = "avares://UltimateMP3PlayerInstaller/Assets/";

    // The app's screens, from the README's picture of four phones (2008×1011): the part inside each frame.
    private readonly IImage[] _shots;
    private readonly Bitmap _icon;
    private readonly InstallingIcon _installing;
    private readonly DispatcherTimer _slides = new() { Interval = TimeSpan.FromSeconds(3.4) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };
    private Control _shown;
    private int _shot, _guideRun;
    private bool _slidesWanted, _awake = true;
    private CableState _cable = CableState.Away;
    private IDisposable? _bob;
    private double _cableY = CableAway;

    // (from seated: the body's top is at the phone's edge; near = the whole metal tip showing, a little away from the port)
    private const double CableAway = 190, CableNear = 58;

    public PhoneView()
    {
        InitializeComponent();
        using (var picture = new Bitmap(AssetLoader.Open(new Uri(Assets + "phone.webp"))))
            _shots = new[] { 86, 566, 1046, 1526 }.Select(x => (IImage)Screenshot(picture, x)).ToArray();
        _icon = new Bitmap(AssetLoader.Open(new Uri(Assets + "icon.png")));
        _installing = new InstallingIcon(_icon);

        ShotFront.Source = _shots[0];
        AppScene.Source = _shots[0];
        _shown = ShowcaseScene;
        _slides.Tick += (_, _) => NextShot();
        _clock.Tick += (_, _) => UpdateClock();
        BuildHome();
    }

    // One of them, with room at the top for the island, which on a real iPhone the app leaves free: that room takes the colour of
    // the picture's first line. Made once into a picture of its own, and from then on it's a plain image to draw.
    private static RenderTargetBitmap Screenshot(Bitmap picture, int x)
    {
        const int width = 396, height = 838, room = 24, top = 78, corner = 44;
        var shot = new RenderTargetBitmap(new PixelSize(width, height));
        using var c = shot.CreateDrawingContext();
        c.DrawImage(picture, new Rect(x + 70, top + 4, width - 140, 2), new Rect(0, 0, width, room + corner));
        // (rounded: the picture's own phone has round corners, and they aren't at the screen's corners any more)
        var below = new Rect(0, room, width, height - room);
        using (c.PushClip(new RoundedRect(below, new Vector(corner, corner), new Vector(corner, corner), default, default)))
            c.DrawImage(picture, new Rect(x, top, width, height - room), below);
        return shot;
    }

    // With the window in the background nobody's watching: the screens stop going by, and the taps wait.
    public bool Awake
    {
        set
        {
            _awake = value;
            _slides.IsEnabled = _slidesWanted && value;
        }
    }

    public void Show(Scene scene)
    {
        Control next = scene switch
        {
            Scene.Lock => LockScene,
            Scene.Home => HomeScene,
            Scene.Guide => GuideScene,
            Scene.App => AppScene,
            _ => ShowcaseScene,
        };
        if (scene != Scene.Guide) _guideRun++;
        _slidesWanted = scene == Scene.Showcase;
        _slides.IsEnabled = _slidesWanted && _awake;
        if (scene == Scene.Lock)
        {
            UpdateClock();
            _clock.Start();
        }
        else _clock.Stop();
        if (ReferenceEquals(next, _shown)) return;

        var old = _shown;
        _shown = next;
        next.IsVisible = true;
        next.ZIndex = 1;
        old.ZIndex = 0;
        next.Opacity = 0;
        Motion.Tween(next, 380, Motion.CubicOut, k =>
        {
            next.Opacity = k;
            Motion.Place(next, 0, 0, Motion.Lerp(0.96, 1, k));
        }, done: () =>
        {
            if (!ReferenceEquals(_shown, old)) old.IsVisible = false;
        });
    }

    // ---- The real app

    private void NextShot()
    {
        var next = _shots[_shot = (_shot + 1) % _shots.Length];
        double width = ShowcaseScene.Bounds.Width;
        ShotBack.Source = ShotFront.Source;
        ShotFront.Source = next;
        Motion.Tween(ShotFront, 620, Motion.QuintOut, k =>
        {
            Motion.Place(ShotFront, width * (1 - k));
            Motion.Place(ShotBack, -width * 0.28 * k);
        });
    }

    public void ShowShot(int index)
    {
        _slidesWanted = false;
        _slides.Stop();
        ShotFront.Source = _shots[index];
    }

    // ---- The lock screen and the cable

    private void UpdateClock()
    {
        var now = DateTime.Now;
        LockTime.Text = now.ToString("HH:mm", L.Culture);
        LockDate.Text = now.ToString("dddd d MMMM", L.Culture);
    }

    public void LockHintShown(bool shown) => LockHint.IsVisible = shown;

    public void SetCable(CableState state)
    {
        if (state == _cable) return;
        _cable = state;
        _bob?.Dispose();
        _bob = null;
        Cable.IsVisible = true;
        double target = state switch { CableState.In => 0, CableState.Near => CableNear, _ => CableAway };
        double from = _cableY;
        Motion.Tween(Cable, state == CableState.In ? 300 : 620, state == CableState.In ? Motion.CubicIn : Motion.QuintOut, k =>
        {
            _cableY = Motion.Lerp(from, target, k);
            Motion.Place(Cable, 0, _cableY);
        }, done: () =>
        {
            if (state == CableState.Away) Cable.IsVisible = false;
            // It hangs there, breathing a little, until there's a phone. In = the small jolt of plugging in.
            if (state == CableState.Near)
                _bob = Motion.Loop(Cable, s => Motion.Place(Cable, 0, _cableY = CableNear - Math.Sin(s * 2.2) * 5), 30);
            if (state == CableState.In) Nudge();
        });
    }

    private void Nudge()
    {
        Motion.Tween(Body, 320, Motion.Linear, k => Motion.Place(Body, 0, Math.Sin(k * Math.PI) * -7 * (1 - k)));
    }

    // The island opens and says something for a moment.
    public void Say(string text, bool good = true)
    {
        IslandLabel.Text = text;
        IslandIcon.IsVisible = good;
        IslandText.Measure(new Size(240, 27));
        double wide = Math.Min(270, IslandText.DesiredSize.Width + 36);
        double from = Island.Width;
        Motion.Tween(Island, 460, Motion.Spring, k =>
        {
            Island.Width = Motion.Lerp(from, wide, k);
            Island.Height = Motion.Lerp(27, 36, k);
            IslandText.Opacity = Math.Clamp(k * 1.4 - 0.3, 0, 1);
        });
        _ = CloseIslandSoon(text);
    }

    private async Task CloseIslandSoon(string text)
    {
        await Task.Delay(2600);
        if (IslandLabel.Text != text) return;
        double from = Island.Width;
        Motion.Tween(Island, 360, Motion.CubicOut, k =>
        {
            Island.Width = Motion.Lerp(from, 92, k);
            Island.Height = Motion.Lerp(36, 27, k);
            IslandText.Opacity = 1 - Math.Min(1, k * 2);
        });
    }

    // ---- The home screen

    private void BuildHome()
    {
        string[] tiles = { "#4A90E2", "#34C759", "#FF9F0A", "#FF375F", "#5E5CE6", "#30B0C7", "#FFD60A", "#8E8E93", "#AF52DE", "#FF6961", "#64D2FF", "#32D74B", "#BF5AF2", "#FF9500", "#0A84FF", "#AC8E68" };
        for (int i = 0; i < 16; i++)
        {
            var cell = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 0, 18), HorizontalAlignment = HorizontalAlignment.Center };
            if (i == 5)
            {
                cell.Children.Add(_installing);
                cell.Children.Add(new TextBlock { Text = "Ultimate MP3", FontSize = 10.5, FontWeight = FontWeight.Medium, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
            }
            else
            {
                cell.Children.Add(new Border { Width = 58, Height = 58, CornerRadius = new CornerRadius(13.5), Background = new ImmutableSolidColorBrush(Color.Parse(tiles[i]), 0.42) });
                cell.Children.Add(new Border { Width = 32, Height = 6, Margin = new Thickness(0, 3, 0, 3), CornerRadius = new CornerRadius(3), Background = new ImmutableSolidColorBrush(Color.Parse("#40FFFFFF")), HorizontalAlignment = HorizontalAlignment.Center });
            }
            HomeGrid.Children.Add(cell);
        }
        for (int i = 0; i < 4; i++)
            HomeDock.Children.Add(new Border { Width = 58, Height = 58, CornerRadius = new CornerRadius(13.5), Background = new ImmutableSolidColorBrush(Color.Parse(tiles[12 + i]), 0.55), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
    }

    public void Installing(double value) => _installing.Set(value);

    // It's there: a little jump, as an app that's just landed.
    public void Installed()
    {
        _installing.Plain();
        Motion.Tween(_installing, 520, Motion.Spring, k => Motion.Place(_installing, 0, 0, Motion.Lerp(0.8, 1, k)));
    }

    // The icon grows into the app.
    public void OpenApp()
    {
        AppScene.Source = _shots[0];
        Show(Scene.App);
    }

    // ---- Settings and alerts

    public sealed record GuideRow(string Label, string Tile = "#8E8E93", bool Target = false, bool Toggle = false, bool Plain = false, string? Header = null);
    public sealed record GuideAlert(string Title, string Text, string Yes, string No, bool Code = false);
    public sealed record GuidePage(string Title, GuideRow[]? Rows = null, GuideAlert? Alert = null, bool Back = true);

    // Shows the pages in turn, a finger tapping what's to tap, and starts over: the same thing the person has to do, on a loop.
    internal static int GuideFrame { get; set; }

    public void Guide(params GuidePage[] pages)
    {
        int run = ++_guideRun;
        // An alert with nothing before it comes up over the lock screen, as it does on a real phone.
        GuideScene.Background = pages[0].Alert != null ? Icons.Brush("Wallpaper") : Brushes.Black;
        Show(Scene.Guide);
        if (Motion.Instant)
        {
            var views = FreshPages(pages);
            views[Math.Min(GuideFrame, views.Length - 1)].IsVisible = true;
            return;
        }
        _ = PlayGuide(run, pages);
    }

    // (made again at each round: a switch flipped in the last one starts off again)
    private Control[] FreshPages(GuidePage[] pages)
    {
        var views = pages.Select(MakePage).ToArray();
        GuideHost.Children.Clear();
        GuideHost.Children.AddRange(views);
        foreach (var v in views) v.IsVisible = false;
        return views;
    }

    private async Task PlayGuide(int run, GuidePage[] pages)
    {
        // A static picture between the taps: nothing is drawn while it waits.
        while (run == _guideRun)
        {
            var views = FreshPages(pages);
            Control? previous = null;
            for (int i = 0; i < views.Length; i++)
            {
                var view = views[i];
                bool alert = view.Tag is GuidePage { Alert: not null };
                Turn(previous, view, alert);
                await Task.Delay(1500);
                while (!_awake && run == _guideRun) await Task.Delay(300);
                if (run != _guideRun) return;
                var target = FindTarget(view);
                if (target != null)
                {
                    TapOn(target, view);
                    await Task.Delay(620);
                    if (run != _guideRun) return;
                }
                previous = view;
            }
            await Task.Delay(1100);
            if (run != _guideRun) return;
        }
    }

    private void Turn(Control? from, Control to, bool alert)
    {
        to.IsVisible = true;
        if (from == null)
        {
            to.Opacity = 0;
            Motion.Place(to);
            Motion.Tween(to, 300, Motion.CubicOut, k => to.Opacity = k);
            foreach (var other in GuideHost.Children)
                if (!ReferenceEquals(other, to)) other.IsVisible = false;
            return;
        }
        if (alert)
        {
            // An alert comes up over what's there (and takes the place of an alert that was).
            if (from.Tag is GuidePage { Alert: not null }) from.IsVisible = false;
            to.Opacity = 0;
            Motion.Tween(to, 260, Motion.CubicOut, k =>
            {
                to.Opacity = k;
                if (to.Tag is GuidePage) AlertScale(to, Motion.Lerp(1.08, 1, k));
            });
            return;
        }
        double w = GuideHost.Bounds.Width;
        Motion.Tween(to, 420, Motion.QuintOut, k =>
        {
            to.Opacity = 1;
            Motion.Place(to, w * (1 - k));
            Motion.Place(from, -w * 0.3 * k);
        }, done: () =>
        {
            from.IsVisible = false;
            Motion.Place(from);
        });
    }

    private static void AlertScale(Control page, double scale)
    {
        if (page is Panel { Children.Count: > 1 } p) Motion.Place(p.Children[1], 0, 0, scale);
    }

    private static Control? FindTarget(Control view)
    {
        if (view.Classes.Contains("target")) return view;
        foreach (var child in view.GetVisualChildrenOf())
            if (FindTarget(child) is { } found) return found;
        return null;
    }

    private void TapOn(Control target, Control view)
    {
        // What's tapped may be a switch: it flips at the touch.
        if (target.Tag is Action act) act();
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), GuideScene);
        if (at == null) return;
        Tap.Margin = new Thickness(at.Value.X - Tap.Width / 2, at.Value.Y - Tap.Height / 2, 0, 0);
        Motion.Tween(Tap, 520, Motion.Linear, k =>
        {
            Tap.Opacity = k < 0.3 ? k / 0.3 * 0.9 : (1 - k) / 0.7 * 0.9;
            Motion.Place(Tap, 0, 0, k < 0.3 ? Motion.Lerp(1.35, 0.85, k / 0.3) : Motion.Lerp(0.85, 1.25, (k - 0.3) / 0.7));
        });
    }

    private static readonly IBrush Group = new ImmutableSolidColorBrush(Color.Parse("#1C1C1E"));
    private static readonly IBrush Hair = new ImmutableSolidColorBrush(Color.Parse("#38383A"));
    private static readonly IBrush Grey = new ImmutableSolidColorBrush(Color.Parse("#8E8E93"));
    private static readonly IBrush Blue = new ImmutableSolidColorBrush(Color.Parse("#0A84FF"));
    private static readonly IBrush Ring = new ImmutableSolidColorBrush(Color.Parse("#FF4FA3"));

    private Control MakePage(GuidePage page)
    {
        var view = new Panel { Tag = page };
        if (page.Alert is { } alert)
        {
            view.Children.Add(new Border { Background = new ImmutableSolidColorBrush(Color.Parse("#99000000")) });
            view.Children.Add(MakeAlert(alert));
            return view;
        }

        var column = new StackPanel { Margin = new Thickness(14, 62, 14, 0), Spacing = 8, Background = null };
        view.Background = Brushes.Black;
        if (page.Back)
            column.Children.Add(new TextBlock { Text = "‹", FontSize = 26, Foreground = Blue, Margin = new Thickness(2, -14, 0, -6), Height = 30 });
        // (the title on one line, as big as it fits: the last page is called like the Apple ID)
        column.Children.Add(new TextBlock { Text = page.Title, FontFamily = Icons.Display, FontSize = Math.Min(28, 256 / (0.56 * Math.Max(1, page.Title.Length))), FontWeight = FontWeight.Bold, Foreground = Brushes.White, Margin = new Thickness(4, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis });

        var rows = new StackPanel();
        var group = new Border { Background = Group, CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = rows };
        foreach (var row in page.Rows ?? [])
        {
            if (row.Header != null)
            {
                column.Children.Add(group);
                rows = new StackPanel();
                group = new Border { Background = Group, CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = rows };
                column.Children.Add(new TextBlock { Text = row.Header, FontSize = 11.5, Foreground = Grey, Margin = new Thickness(14, 10, 0, 0) });
            }
            if (rows.Children.Count > 0)
                rows.Children.Add(new Border { Height = 0.7, Background = Hair, Margin = new Thickness(row.Plain ? 14 : 50, 0, 0, 0) });
            rows.Children.Add(MakeRow(row));
        }
        column.Children.Add(group);
        view.Children.Add(column);
        return view;
    }

    private static Control MakeRow(GuideRow row)
    {
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 44, Margin = new Thickness(12, 0) };
        if (!row.Plain)
            line.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(6.5), Background = new ImmutableSolidColorBrush(Color.Parse(row.Tile)), Margin = new Thickness(0, 0, 12, 0) });
        var label = new TextBlock { Text = row.Label, FontSize = 14.5, Foreground = row.Plain && row.Target && !row.Toggle ? Blue : Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(label, 1);
        line.Children.Add(label);

        Border? knob = null, track = null;
        if (row.Toggle)
        {
            knob = new Border { Width = 25, Height = 25, CornerRadius = new CornerRadius(12.5), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2.5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            track = new Border { Width = 50, Height = 30, CornerRadius = new CornerRadius(15), Background = new ImmutableSolidColorBrush(Color.Parse("#39393D")), Child = knob };
            Grid.SetColumn(track, 2);
            line.Children.Add(track);
        }
        else if (!row.Plain)
        {
            var chevron = new TextBlock { Text = "›", FontSize = 18, Foreground = Grey, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chevron, 2);
            line.Children.Add(chevron);
        }

        var cell = new Panel { Children = { line } };
        if (!row.Target) return cell;

        // What to tap: lit, and the finger comes down on it.
        cell.Children.Add(new Border { BorderBrush = Ring, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(12), Margin = new Thickness(2), IsHitTestVisible = false });
        cell.Classes.Add("target");
        if (track != null)
            cell.Tag = (Action)(() =>
            {
                track.Background = new ImmutableSolidColorBrush(Color.Parse("#34C759"));
                Motion.Tween(knob!, 220, Motion.CubicOut, k => Motion.Place(knob!, 20 * k));
            });
        return cell;
    }

    private static Control MakeAlert(GuideAlert a)
    {
        var box = new StackPanel { Width = 244 };
        var text = new StackPanel { Margin = new Thickness(18, 18, 18, 16), Spacing = 5 };
        text.Children.Add(new TextBlock { Text = a.Title, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        if (a.Code)
            text.Children.Add(new TextBlock { Text = "1 2 3 4 5 6", FontFamily = Icons.Display, FontSize = 30, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 2) });
        text.Children.Add(new TextBlock { Text = a.Text, FontSize = 12.5, LineHeight = 16, Foreground = new ImmutableSolidColorBrush(Color.Parse("#D0FFFFFF")), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        box.Children.Add(text);
        box.Children.Add(new Border { Height = 0.7, Background = Hair });

        Control Button(string label, bool target, bool bold)
        {
            var cell = new Panel { Height = 44 };
            cell.Children.Add(new TextBlock { Text = label, FontSize = 15, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, Foreground = Blue, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center });
            if (target)
            {
                cell.Children.Add(new Border { BorderBrush = Ring, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(12), Margin = new Thickness(3), IsHitTestVisible = false });
                cell.Classes.Add("target");
            }
            return cell;
        }

        if (a.No.Length == 0) box.Children.Add(Button(a.Yes, true, true));
        else if (a.Yes.Length + a.No.Length > 22)
        {
            box.Children.Add(Button(a.Yes, true, true));
            box.Children.Add(new Border { Height = 0.7, Background = Hair });
            box.Children.Add(Button(a.No, false, false));
        }
        else
        {
            var pair = new Grid { ColumnDefinitions = new ColumnDefinitions("*,0.7,*") };
            var no = Button(a.No, false, false);
            var line = new Border { Background = Hair };
            var yes = Button(a.Yes, true, true);
            Grid.SetColumn(line, 1);
            Grid.SetColumn(yes, 2);
            pair.Children.Add(no);
            pair.Children.Add(line);
            pair.Children.Add(yes);
            box.Children.Add(pair);
        }

        return new Border
        {
            Background = new ImmutableSolidColorBrush(Color.Parse("#2C2C2E")),
            CornerRadius = new CornerRadius(20),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = box,
        };
    }

}

internal static class VisualWalk
{
    public static IEnumerable<Control> GetVisualChildrenOf(this Control c) => Avalonia.VisualTree.VisualExtensions.GetVisualChildren(c).OfType<Control>();
}

// What the phone shows for each thing to do on it: iOS's own words, in the person's language.
public static class Guides
{
    private static PhoneView.GuideRow R(string label, string tile, bool target = false, bool toggle = false) => new(L.T(label), tile, target, toggle);

    public static PhoneView.GuidePage[] DeveloperMode() => new[]
    {
        new PhoneView.GuidePage(L.T("Settings"), new[]
        {
            R("General", "#8E8E93"), R("Accessibility", "#0A84FF"), R("Notifications", "#FF3B30"), R("Sounds & Haptics", "#FF2D55"), R("Battery", "#34C759"), R("Privacy & Security", "#0A84FF", true),
        }, Back: false),
        new PhoneView.GuidePage(L.T("Privacy & Security"), new[]
        {
            R("Location Services", "#0A84FF"), R("Tracking", "#FF9F0A"), R("Contacts", "#8E8E93"), R("Photos", "#FF375F"),
            new PhoneView.GuideRow(L.T("Developer Mode"), "#5E5CE6", true, Header: L.T("SECURITY")),
        }),
        new PhoneView.GuidePage(L.T("Developer Mode"), new[] { new PhoneView.GuideRow(L.T("Developer Mode"), Target: true, Toggle: true, Plain: true) }),
        new PhoneView.GuidePage("", Alert: new PhoneView.GuideAlert(L.T("Turn on Developer Mode?"), "", L.T("Restart"), L.T("Cancel"))),
        new PhoneView.GuidePage("", Alert: new PhoneView.GuideAlert(L.T("Turn on Developer Mode?"), "", L.T("Turn On"), L.T("Cancel"))),
    };

    public static PhoneView.GuidePage[] TrustAccount(string appleId) => new[]
    {
        new PhoneView.GuidePage(L.T("Settings"), new[]
        {
            R("General", "#8E8E93", true), R("Accessibility", "#0A84FF"), R("Notifications", "#FF3B30"), R("Sounds & Haptics", "#FF2D55"), R("Battery", "#34C759"), R("Privacy & Security", "#0A84FF"),
        }, Back: false),
        new PhoneView.GuidePage(L.T("General"), new[]
        {
            R("About", "#8E8E93"), R("Software Update", "#8E8E93"), R("iPhone Storage", "#8E8E93"), R("Date & Time", "#8E8E93"), R("VPN & Device Management", "#8E8E93", true),
        }),
        new PhoneView.GuidePage(L.T("VPN & Device Management"), new[] { new PhoneView.GuideRow(appleId, "#5E5CE6", true, Header: L.T("DEVELOPER APP")) }),
        new PhoneView.GuidePage(appleId, new[] { new PhoneView.GuideRow(L.T("Trust") + " " + appleId, Target: true, Plain: true) }),
        new PhoneView.GuidePage("", Alert: new PhoneView.GuideAlert(L.T("Trust") + " " + appleId + "?", "", L.T("Trust"), L.T("Cancel"))),
    };

    public static PhoneView.GuidePage[] TrustComputer() => new[]
    {
        new PhoneView.GuidePage("", Alert: new PhoneView.GuideAlert(L.T("Trust This Computer?"), L.T("Your settings and data will be accessible from this computer when connected."), L.T("Trust"), L.T("Don't Trust"))),
    };

    public static PhoneView.GuidePage[] AppleCode() => new[]
    {
        new PhoneView.GuidePage("", Alert: new PhoneView.GuideAlert(L.T("Apple ID Sign In Requested"), L.T("Your Apple ID is being used to sign in on a new device."), L.T("Allow"), L.T("Don't Allow"))),
        new PhoneView.GuidePage("", Alert: new PhoneView.GuideAlert(L.T("Apple ID Verification Code"), L.T("Enter this code on your other device."), L.T("Done"), "", Code: true)),
    };
}
