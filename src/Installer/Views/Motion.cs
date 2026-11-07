using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Installer;

// Everything that moves here goes through this: position, size and opacity of things already drawn, on the screen's own clock,
// and nothing runs when nothing moves. No layout per frame, no effects: the graphics card does it for free and the window
// sits at zero between one motion and the next.
public static class Motion
{
    // Offscreen pictures: every motion at its end, at once.
    public static bool Instant { get; set; }

    private static readonly ConditionalWeakTable<object, object> Running = new();

    // apply(eased progress 0..1) every frame for ms milliseconds. A new tween with the same key takes over from the old one.
    public static void Tween(Visual owner, double ms, Func<double, double> ease, Action<double> apply, double delay = 0, object? key = null, Action? done = null)
    {
        key ??= owner;
        var top = TopLevel.GetTopLevel(owner);
        if (Instant || top == null || ms <= 0)
        {
            Running.Remove(key);
            apply(1);
            done?.Invoke();
            return;
        }
        var token = new object();
        Running.AddOrUpdate(key, token);
        TimeSpan? start = null;
        void Frame(TimeSpan now)
        {
            if (!Running.TryGetValue(key, out var current) || !ReferenceEquals(current, token)) return;
            start ??= now;
            double t = Math.Clamp(((now - start.Value).TotalMilliseconds - delay) / ms, 0, 1);
            apply(ease(t));
            if (t < 1) top.RequestAnimationFrame(Frame);
            else done?.Invoke();
        }
        apply(ease(0));
        top.RequestAnimationFrame(Frame);
    }

    public static void Stop(object key) => Running.Remove(key);

    // Calls frame(seconds so far) until disposed, at most 60 times a second however fast the screen is: this is for things that
    // keep going for a while (a spinner, a wait), where more than 60 would only be heat.
    public static IDisposable Loop(Visual owner, Action<double> frame, int fps = 60)
    {
        var loop = new Looper(owner, frame, fps);
        loop.Next();
        return loop;
    }

    private sealed class Looper(Visual owner, Action<double> frame, int fps) : IDisposable
    {
        private bool _stopped;
        private long _start, _last;

        public void Next()
        {
            if (Instant)
            {
                frame(0);
                return;
            }
            if (_stopped || TopLevel.GetTopLevel(owner) is not { } top) return;
            top.RequestAnimationFrame(Tick);
        }

        private void Tick(TimeSpan _)
        {
            if (_stopped) return;
            // On a page that isn't showing: nothing at all, not even a frame, but a look now and then in case it comes back.
            if (!owner.IsEffectivelyVisible)
            {
                DispatcherTimer.RunOnce(LookAgain, TimeSpan.FromMilliseconds(250));
                return;
            }
            long now = Stopwatch.GetTimestamp();
            if (_start == 0) _start = now;
            // (a whisker under the period: a screen's frames never land exactly on it, and a miss would halve the rate)
            if ((now - _last) * (fps + 2) >= Stopwatch.Frequency)
            {
                _last = now;
                frame((double)(now - _start) / Stopwatch.Frequency);
            }
            Next();
        }

        // (a plain timer, no frame asked for: a frame wakes the whole drawing machinery, even with nothing to draw)
        private void LookAgain()
        {
            if (_stopped) return;
            if (owner.IsEffectivelyVisible) Next();
            else DispatcherTimer.RunOnce(LookAgain, TimeSpan.FromMilliseconds(250));
        }

        public void Dispose() => _stopped = true;
    }

    // Where a thing is drawn, without touching the layout. The transform is one object kept on the control.
    public static void Place(Visual v, double x = 0, double y = 0, double scale = 1)
    {
        if (v.RenderTransform is not MatrixTransform m)
        {
            m = new MatrixTransform();
            v.RenderTransform = m;
        }
        m.Matrix = new Matrix(scale, 0, 0, scale, x, y);
    }

    public static double Linear(double t) => t;
    public static double CubicOut(double t) => 1 - Math.Pow(1 - t, 3);
    public static double QuintOut(double t) => 1 - Math.Pow(1 - t, 5);
    public static double CubicIn(double t) => t * t * t;
    public static double InOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    // Past the end and back, as things land on iOS.
    public static double Spring(double t)
    {
        const double c = 1.56;
        double p = t - 1;
        return 1 + (c + 1) * p * p * p + c * p * p;
    }

    public static double Lerp(double a, double b, double k) => a + (b - a) * k;
}
