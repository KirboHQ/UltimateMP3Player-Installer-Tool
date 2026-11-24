using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using System.Runtime.InteropServices;

namespace Installer;

// The light behind everything: two soft blobs of colour, worked out once per colour pair, away from the UI thread, into a picture
// with a very fine grain. A plain gradient on these dark tones shows rings (256 levels aren't enough for it); the grain hides them.
// Going from one step to the next is one picture fading in over the other, and the rest of the time it's a still picture.
public sealed class Aurora : Image
{
    private const int W = 800, H = 500;
    private static readonly Dictionary<(string, string), Task<int[]>> Baked = new();

    public Aurora()
    {
        Stretch = Stretch.Fill;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.LowQuality);
    }

    public static void Prepare((string, string) colors)
    {
        if (!Baked.ContainsKey(colors)) Baked[colors] = Task.Run(() => Bake(colors.Item1, colors.Item2));
    }

    public void Paint((string, string) colors)
    {
        Prepare(colors);
        var pixels = Baked[colors].Result;
        var picture = new WriteableBitmap(new PixelSize(W, H), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var buffer = picture.Lock()) Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        var old = Source as IDisposable;
        Source = picture;
        old?.Dispose();
    }

    internal static int[] Bake(string first, string second)
    {
        var a = Color.Parse(first);
        var b = Color.Parse(second);
        var pixels = new int[W * H];
        var random = new Random(7);
        const double baseR = 0x0C, baseG = 0x0A, baseB = 0x14;
        double aspect = (double)W / H;
        for (int y = 0; y < H; y++)
        {
            double v = (y + 0.5) / H;
            for (int x = 0; x < W; x++)
            {
                double u = (x + 0.5) / W;
                double dx1 = (u - 0.25) * aspect, dy1 = v - 0.40, dx2 = (u - 0.04) * aspect, dy2 = v - 0.98;
                double k1 = 0.56 * Math.Exp(-(dx1 * dx1 + dy1 * dy1) / (2 * 0.40 * 0.40));
                double k2 = 0.52 * Math.Exp(-(dx2 * dx2 + dy2 * dy2) / (2 * 0.34 * 0.34));
                double r = baseR + (a.R - baseR) * k1, g = baseG + (a.G - baseG) * k1, l = baseB + (a.B - baseB) * k1;
                r += (b.R - r) * k2;
                g += (b.G - g) * k2;
                l += (b.B - l) * k2;
                // Two random numbers added: a grain of about one level, stronger in the middle of its range, invisible as noise.
                double grain = random.NextDouble() + random.NextDouble() - 1;
                int R = Math.Clamp((int)(r + grain + 0.5), 0, 255), G = Math.Clamp((int)(g + grain + 0.5), 0, 255), B = Math.Clamp((int)(l + grain + 0.5), 0, 255);
                pixels[y * W + x] = unchecked((int)0xFF000000) | R << 16 | G << 8 | B;
            }
        }
        return pixels;
    }
}

// A thin bar that glides to its value by itself, however rarely the value comes. Value below 0 = no idea how long: a piece of it
// goes back and forth. The running part is a rounded rectangle moved over a clip, so its ends stay round at any length.
public sealed class Bar : Control
{
    private static readonly IBrush Track = new ImmutableSolidColorBrush(Color.Parse("#22FFFFFF"));
    private static readonly IBrush Fill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#6D4DFF"), 0), new GradientStop(Color.Parse("#A54CF5"), 0.5), new GradientStop(Color.Parse("#FF4FA3"), 1) },
    };

    private double _shown, _from, _width = 0, _x;
    private IDisposable? _wander;

    public void Set(double value)
    {
        if (value < 0)
        {
            if (_wander != null) return;
            _wander = Motion.Loop(this, seconds =>
            {
                _x = (Math.Sin(seconds * 2.4) + 1) / 2 * 0.72;
                _width = 0.28;
                InvalidateVisual();
            });
            return;
        }
        _wander?.Dispose();
        _wander = null;
        _x = 0;
        _from = _shown;
        double target = Math.Clamp(value, 0, 1);
        Motion.Tween(this, 420, Motion.CubicOut, k =>
        {
            _shown = Motion.Lerp(_from, target, k);
            _width = _shown;
            InvalidateVisual();
        });
    }

    public void Reset()
    {
        _wander?.Dispose();
        _wander = null;
        Motion.Stop(this);
        _shown = _from = _x = _width = 0;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _wander?.Dispose();
        _wander = null;
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height, r = h / 2;
        context.DrawRectangle(Track, null, new RoundedRect(new Rect(0, 0, w, h), r));
        if (_width <= 0) return;
        context.DrawRectangle(Fill, null, new RoundedRect(new Rect(_x * w, 0, Math.Max(h, _width * w), h), r));
    }
}

// A little ring turning. Drawn once; each frame only its angle changes.
public sealed class Spinner : Control
{
    private static readonly Pen Stroke = new(new ImmutableSolidColorBrush(Color.Parse("#C9B8FF")), 2.4, null, PenLineCap.Round);
    private static readonly Pen Track = new(new ImmutableSolidColorBrush(Color.Parse("#30FFFFFF")), 2.4);
    private IDisposable? _loop;
    private readonly RotateTransform _turn = new();

    public Spinner()
    {
        Width = Height = 20;
        RenderTransform = _turn;
    }

    private void Update()
    {
        _loop?.Dispose();
        _loop = null;
        if (IsVisible && VisualRoot != null) _loop = Motion.Loop(this, s => _turn.Angle = s * 320 % 360);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _loop?.Dispose();
        _loop = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) Update();
    }

    public override void Render(DrawingContext context)
    {
        double r = Width / 2 - 2;
        var center = new Point(Width / 2, Height / 2);
        context.DrawEllipse(null, Track, center, r, r);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(center.X + r, center.Y), false);
            c.ArcTo(new Point(center.X, center.Y - r), new Size(r, r), 0, false, SweepDirection.CounterClockwise);
            c.EndFigure(false);
        }
        context.DrawGeometry(null, Stroke, g);
    }
}

// The app's icon as iOS shows it while it installs: dimmed, with a clear wedge sweeping over it.
public sealed class InstallingIcon : Control
{
    private static readonly IBrush Veil = new ImmutableSolidColorBrush(Color.Parse("#A0000000"));
    private readonly Bitmap _icon;
    private double _value = 1, _from;

    public InstallingIcon(Bitmap icon)
    {
        _icon = icon;
        Width = Height = 58;
    }

    // Below 0: the plain icon. Otherwise how far the wedge went.
    public void Set(double value)
    {
        _from = _value < 0 || _value >= 1 ? 0 : _value;
        Motion.Tween(this, 380, Motion.CubicOut, k =>
        {
            _value = Motion.Lerp(_from, value, k);
            InvalidateVisual();
        });
    }

    public void Plain()
    {
        Motion.Stop(this);
        _value = 1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var box = new Rect(Bounds.Size);
        using (context.PushClip(new RoundedRect(box, 13.5)))
        {
            context.DrawImage(_icon, box);
            if (_value >= 1) return;
            context.FillRectangle(Veil, box);
            double r = box.Width * 0.30, angle = Math.Clamp(_value, 0, 0.9999) * 2 * Math.PI;
            var c = box.Center;
            var wedge = new StreamGeometry();
            using (var g = wedge.Open())
            {
                g.BeginFigure(c, true);
                g.LineTo(new Point(c.X, c.Y - r));
                g.ArcTo(new Point(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle)), new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise);
                g.EndFigure(true);
            }
            using (context.PushGeometryClip(wedge)) context.DrawImage(_icon, box);
            context.DrawEllipse(null, new Pen(Brushes.White, 1.5), c, r + 3, r + 3);
        }
    }
}

// Paper falling, once, when it's done: rectangles of the logo's colours, for under three seconds, and then nothing at all.
public sealed class Confetti : Control
{
    private static readonly IBrush[] Colors = new[] { "#6D4DFF", "#A54CF5", "#FF4FA3", "#FFC24B", "#3DDC97", "#FFFFFF" }
        .Select(c => (IBrush)new ImmutableSolidColorBrush(Color.Parse(c))).ToArray();

    private record struct Piece(double X, double Y, double Vx, double Vy, double Spin, double Turn, double W, double H, IBrush Brush);

    private Piece[] _pieces = [];
    private double _seconds = -1;
    private IDisposable? _loop;
    private const double Length = 2.8, Air = 1.6, Fall = 300;

    // For a picture: how it looks a given time after the burst.
    internal void At(double seconds)
    {
        Motion.Stop(this);
        _loop?.Dispose();
        _seconds = seconds;
        IsVisible = true;
        InvalidateVisual();
    }

    public void Burst()
    {
        var random = new Random();
        _pieces = new Piece[150];
        for (int i = 0; i < _pieces.Length; i++)
        {
            bool left = i % 2 == 0;
            double angle = (left ? -60 : -120) + (random.NextDouble() - 0.5) * 50, speed = 520 + random.NextDouble() * 760;
            _pieces[i] = new Piece(left ? 0.02 : 0.98, 0.72 + random.NextDouble() * 0.2,
                speed * Math.Cos(angle * Math.PI / 180), speed * Math.Sin(angle * Math.PI / 180),
                random.NextDouble() * 6.28, (random.NextDouble() - 0.5) * 14,
                6 + random.NextDouble() * 6, 9 + random.NextDouble() * 8, Colors[i % Colors.Length]);
        }
        _seconds = 0;
        IsVisible = true;
        _loop?.Dispose();
        _loop = Motion.Loop(this, s =>
        {
            _seconds = s;
            InvalidateVisual();
            if (s > Length)
            {
                _loop?.Dispose();
                _pieces = [];
                IsVisible = false;
            }
        });
    }

    public override void Render(DrawingContext context)
    {
        double t = _seconds, fade = Math.Clamp((Length - t) / 0.7, 0, 1), w = Bounds.Width, h = Bounds.Height;
        if (t < 0) return;
        using (context.PushOpacity(fade))
            foreach (var p in _pieces)
            {
                // Falling through air: it slows down sideways and reaches a steady speed downwards.
                double drag = (1 - Math.Exp(-Air * t)) / Air;
                double x = p.X * w + p.Vx * drag, y = p.Y * h + Fall * t + (p.Vy - Fall) * drag;
                if (y > h + 20) continue;
                double a = p.Spin + p.Turn * t, flip = Math.Abs(Math.Cos(a * 1.3));
                var m = Matrix.CreateRotation(a) * Matrix.CreateTranslation(x, y);
                using (context.PushTransform(m))
                    context.FillRectangle(p.Brush, new Rect(-p.W / 2, -p.H * flip / 2, p.W, p.H * flip));
            }
    }
}

// A code for the phone's camera: one pixel a module, made big without smoothing, so its edges stay sharp at any scale.
public sealed class QrCode : Border
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private string? _shown;

    public QrCode()
    {
        // (white all round: cameras want a quiet margin, which the matrix brings too)
        Background = Brushes.White;
        CornerRadius = new CornerRadius(12);
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.None);
        Child = _image;
    }

    public void Show(string text)
    {
        if (text == _shown) return;
        _shown = text;
        // (the lowest correction: fewer, bigger modules, and a screen doesn't get dirty like paper)
        using var data = new QRCoder.QRCodeGenerator().CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.L);
        var rows = data.ModuleMatrix;
        int n = rows.Count;
        var pixels = new int[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                pixels[y * n + x] = rows[y][x] ? unchecked((int)0xFF000000) : -1;
        var picture = new WriteableBitmap(new PixelSize(n, n), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var buffer = picture.Lock()) Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        var old = _image.Source as IDisposable;
        _image.Source = picture;
        old?.Dispose();
    }
}

// How many of the signature's seven days are left.
public sealed class Week : Control
{
    private int _left;
    private IBrush _brush = Brushes.White;

    public void Show(double days, IBrush brush)
    {
        _left = Math.Clamp((int)Math.Ceiling(days), 0, Memory.SignedDays);
        _brush = brush;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        const double gap = 5;
        double w = (Bounds.Width - gap * (Memory.SignedDays - 1)) / Memory.SignedDays, h = Bounds.Height;
        for (int i = 0; i < Memory.SignedDays; i++)
            context.DrawRectangle(i < _left ? _brush : Icons.Brush("Line"), null, new RoundedRect(new Rect(i * (w + gap), 0, w, h), h / 2));
    }
}

// A word or two in a pill, in the colour of what it means.
public sealed class Badge : Border
{
    private readonly TextBlock _text = new() { FontSize = 11.5, FontWeight = FontWeight.Bold, LetterSpacing = 0.8 };

    public Badge()
    {
        CornerRadius = new CornerRadius(11);
        Padding = new Thickness(10, 3);
        BorderThickness = new Thickness(1);
        Child = _text;
    }

    public void Set(string text, IBrush brush)
    {
        _text.Text = text;
        _text.Foreground = brush;
        BorderBrush = brush;
    }
}

public static class Icons
{
    // A drawing that isn't square, stretched, goes to the top left of its box: so the drawing gets a box of its own proportions, in
    // the middle of the square one.
    public static Control Make(string key, IBrush? stroke = null, double size = 18)
    {
        var path = new Avalonia.Controls.Shapes.Path { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        path.Classes.Add("icon");
        if (Application.Current!.TryFindResource(key, out var g) && g is Geometry geometry)
        {
            path.Data = geometry;
            var (w, h) = (geometry.Bounds.Width, geometry.Bounds.Height);
            path.Width = size * w / Math.Max(w, h);
            path.Height = size * h / Math.Max(w, h);
        }
        if (stroke != null) path.Stroke = stroke;
        return new Panel { Width = size, Height = size, Children = { path } };
    }

    public static FontFamily Display => Application.Current!.TryFindResource("Display", out var f) && f is FontFamily family ? family : FontFamily.Default;

    public static IBrush Brush(string key) => Application.Current!.TryFindResource(key, out var b) && b is IBrush brush ? brush : Brushes.White;
}
