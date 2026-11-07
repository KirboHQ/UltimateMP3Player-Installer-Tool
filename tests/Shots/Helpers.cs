using System.Diagnostics;
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Installer.Tests;

// A one-file web server, for the download's checks: nothing leaves this computer.
public sealed class OneFile : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;
    public string Url { get; }

    public OneFile(string content)
    {
        var port = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        port.Start();
        int number = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        Url = $"http://127.0.0.1:{number}/file";
        _listener.Prefixes.Add($"http://127.0.0.1:{number}/");
        _listener.Start();
        _serving = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var c = await _listener.GetContextAsync();
                    var body = Encoding.UTF8.GetBytes(content);
                    c.Response.ContentLength64 = body.Length;
                    await c.Response.OutputStream.WriteAsync(body);
                    c.Response.Close();
                }
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Close();
        await _serving;
    }
}

// The real window on screen, going through every screen with its motions, counting what the screen really got.
public static class Perf
{
    public static async Task RunAsync(string[] names, string folder)
    {
        var done = new TaskCompletionSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var window = new MainWindow { ShowActivated = false };
                window.Show();
                await Task.Delay(1200);
                var gaps = new List<(string Name, double Worst, double Before, double Fps, int Frames)>();
                var process = Process.GetCurrentProcess();

                foreach (var name in names)
                {
                    var times = new List<long>();
                    bool counting = true;
                    void Frame(TimeSpan _)
                    {
                        if (!counting) return;
                        times.Add(Stopwatch.GetTimestamp());
                        window.RequestAnimationFrame(Frame);
                    }
                    window.RequestAnimationFrame(Frame);
                    window.Preview(name);
                    await Task.Delay(1300);
                    counting = false;
                    // Only while it moves (the motions run for about 600 ms, and with nothing to draw the frames counted here come
                    // at whatever pace the idle compositor has, which says nothing). The first gap is the page being made, before
                    // anything starts moving, so it doesn't show as a jump: told apart from a stumble in the middle of a motion.
                    double worst = 0, moving = 0, before = 0;
                    int counted = 0;
                    for (int i = 1; i < times.Count && moving < 600; i++)
                    {
                        double ms = (times[i] - times[i - 1]) * 1000.0 / Stopwatch.Frequency;
                        moving += ms;
                        counted++;
                        if (i <= 2) before = Math.Max(before, ms);
                        else worst = Math.Max(worst, ms);
                    }
                    gaps.Add((name, worst, before, counted * 1000 / moving, counted));
                }

                // Sitting still: what it costs when nothing moves, on the two kinds of page that wait for a long time.
                var rest = new List<string>();
                foreach (var name in Environment.GetEnvironmentVariable("PERF_REST")?.Split(',') ?? new[] { "welcome", "connect", "account", "activate", "install", "done" })
                {
                    window.Preview(name);
                    // (long enough for the runtime's own start-up work to end: its background compiling isn't what the window costs)
                    await Task.Delay(Math.Max(name == "done" ? 3500 : 1200, int.Parse(Environment.GetEnvironmentVariable("PERF_SETTLE") ?? "0")));
                    process.Refresh();
                    var cpu = process.TotalProcessorTime;
                    var clock = Stopwatch.StartNew();
                    await Task.Delay(4000);
                    process.Refresh();
                    rest.Add($"{name}: {(process.TotalProcessorTime - cpu).TotalMilliseconds / clock.Elapsed.TotalSeconds:0} ms of CPU per second");
                }

                var lines = new List<string>();
                foreach (var g in gaps) lines.Add($"{g.Name,-18} {g.Fps,5:0} fps   stumble {g.Worst,5:0.0} ms   making the page {g.Before,5:0.0} ms");
                lines.Add($"average {gaps.Average(g => g.Fps):0} fps, worst stumble {gaps.Max(g => g.Worst):0.0} ms, slowest page {gaps.Max(g => g.Before):0.0} ms");
                lines.AddRange(rest);
                lines.Add($"working set {process.WorkingSet64 / (1024 * 1024)} MB");
                foreach (var l in lines) Console.WriteLine(l);
                File.WriteAllLines(Path.Combine(folder, "perf.txt"), lines);
                window.Close();
            }
            finally { done.TrySetResult(); }
        });
        var pump = new CancellationTokenSource();
        _ = done.Task.ContinueWith(_ => pump.Cancel());
        Dispatcher.UIThread.MainLoop(pump.Token);
        await done.Task;
    }
}
