using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Installer;

public enum OutKind { Line, Stage, Prompt }
public enum PromptKind { None, AppleId, Password, Code, Confirm, Choice }

public readonly record struct Out(OutKind Kind, string Text = "", double? Percent = null, PromptKind Prompt = PromptKind.None);

// What xtool prints turned into events. Its stages ("[Signing]  42%") come back over themselves with \r and its questions end
// without a newline, so what's on the line so far is looked at too, not only whole lines.
public sealed partial class XtoolParser
{
    private readonly StringBuilder _line = new();
    private string _seen = "";

    [GeneratedRegex(@"^\[(.+?)\]\s*(?:(\d+)%|\.\.\.)?$")]
    private static partial Regex StageLine();

    public List<Out> Feed(string chunk)
    {
        var events = new List<Out>();
        foreach (char c in chunk)
        {
            if (c == '\n' || c == '\r')
            {
                Finish(events);
                _line.Clear();
                _seen = "";
            }
            else _line.Append(c);
        }
        Partial(events);
        return events;
    }

    // The end of the output: a last line that had no newline.
    public List<Out> End()
    {
        var events = new List<Out>();
        Finish(events);
        _line.Clear();
        _seen = "";
        return events;
    }

    private void Finish(List<Out> events)
    {
        string text = _line.ToString();
        if (text.Length == 0 || text == _seen) return;
        var known = Classify(text);
        events.Add(known ?? new Out(OutKind.Line, text.Trim()));
    }

    private void Partial(List<Out> events)
    {
        string text = _line.ToString();
        if (text.Length == 0 || text == _seen) return;
        if (Classify(text) is { } known)
        {
            events.Add(known);
            _seen = text;
        }
    }

    private static Out? Classify(string text)
    {
        var stage = StageLine().Match(text.Trim());
        if (stage.Success)
            return new Out(OutKind.Stage, stage.Groups[1].Value, stage.Groups[2].Success ? double.Parse(stage.Groups[2].Value) / 100 : null);
        if (text.EndsWith("Apple ID: ")) return new Out(OutKind.Prompt, text, null, PromptKind.AppleId);
        if (text.EndsWith("Password: ")) return new Out(OutKind.Prompt, text, null, PromptKind.Password);
        if (text.EndsWith("Code: ")) return new Out(OutKind.Prompt, text, null, PromptKind.Code);
        if (text.EndsWith("(yes/no): ")) return new Out(OutKind.Prompt, text, null, PromptKind.Confirm);
        if (text.StartsWith("Choice (") && text.EndsWith("): ")) return new Out(OutKind.Prompt, text, null, PromptKind.Choice);
        if (text.StartsWith("Please tap 'trust'") || text.StartsWith("Please unlock") || text.StartsWith("Continuing...")) return new Out(OutKind.Line, text.Trim());
        return null;
    }
}

public sealed class XtoolException(string details) : Exception(details)
{
    public string Details { get; } = details;
}

// One run of xtool, in our Linux on Windows, as it is elsewhere.
public sealed partial class XtoolRun : IDisposable
{
    private readonly Process _process;
    private readonly List<string> _log = new();

    // (what a person may copy and post in a report: without who they are)
    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+")]
    private static partial Regex Private();
    [GeneratedRegex(@"udid: \S+")]
    private static partial Regex UdidMark();

    private XtoolRun(Process process) => _process = process;

    public static XtoolRun Start(params string[] args)
    {
        var psi = new ProcessStartInfo(OperatingSystem.IsWindows() ? "wsl.exe" : Setup.NativeXtool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["WSL_UTF8"] = "1";
        var all = OperatingSystem.IsWindows() ? Setup.WslArgs(new[] { "/opt/ump/xtool/AppRun" }.Concat(args).ToArray()) : args;
        foreach (var a in all) psi.ArgumentList.Add(a);
        return new XtoolRun(Process.Start(psi) ?? throw new IOException("xtool"));
    }

    // Reads it to the end, calling back (on the UI thread if that's where it was called from) as things come. Returns the exit code.
    public async Task<int> ReadAsync(Func<Out, Task> on, CancellationToken ct)
    {
        using var kill = ct.Register(Kill);
        var parser = new XtoolParser();
        var gate = new SemaphoreSlim(1, 1);

        async Task Handle(List<Out> events)
        {
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                foreach (var e in events)
                {
                    // (the password never comes out of xtool: what it reads, it doesn't print)
                    if (e.Kind == OutKind.Line && e.Text.Length > 0) _log.Add(e.Text);
                    try { await on(e); }
                    catch
                    {
                        // (ended here, or the reader of the other stream would wait for an end that never comes)
                        Kill();
                        throw;
                    }
                }
            }
            finally { gate.Release(); }
        }

        async Task Pump(StreamReader reader)
        {
            var buffer = new char[4096];
            int n;
            while ((n = await reader.ReadAsync(buffer, CancellationToken.None)) > 0)
                await Handle(parser.Feed(new string(buffer, 0, n)));
        }

        // Errors go through the same parser: xtool's order between the two doesn't matter to what's looked for here.
        await Task.WhenAll(Pump(_process.StandardOutput), Pump(_process.StandardError));
        await Handle(parser.End());
        await _process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return _process.ExitCode;
    }

    public async Task SendAsync(string line)
    {
        await _process.StandardInput.WriteLineAsync(line);
        await _process.StandardInput.FlushAsync();
    }

    public string Log => Private().Replace(UdidMark().Replace(string.Join('\n', _log.TakeLast(30)), "udid: <hidden>"), "<email>");

    private void Kill()
    {
        try { _process.Kill(true); }
        catch { }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}

public static class Xtool
{
    // Who's logged in, or null.
    public static async Task<string?> AccountAsync(CancellationToken ct)
    {
        using var run = XtoolRun.Start("auth", "status");
        string? account = null;
        int code = await run.ReadAsync(e =>
        {
            const string mark = "- Apple ID: ";
            if (e.Kind == OutKind.Line && e.Text.StartsWith(mark)) account = e.Text[mark.Length..].Trim();
            return Task.CompletedTask;
        }, ct);
        return code == 0 ? account : null;
    }

    // askCode comes back with what the person typed, or null when they went back.
    public static async Task LoginAsync(string email, string password, Func<Task<string?>> askCode, CancellationToken ct)
    {
        using var run = XtoolRun.Start("auth", "login", "-m", "password");
        int code = await run.ReadAsync(async e =>
        {
            if (e.Kind != OutKind.Prompt) return;
            switch (e.Prompt)
            {
                case PromptKind.AppleId: await run.SendAsync(email); break;
                case PromptKind.Password: await run.SendAsync(password); break;
                case PromptKind.Code:
                    var typed = await askCode();
                    if (typed == null) throw new OperationCanceledException();
                    await run.SendAsync(typed);
                    break;
                // shortcut: an account in several teams signs with the first, which is what a free one has; a chooser if anyone asks.
                case PromptKind.Choice: await run.SendAsync("0"); break;
            }
        }, ct);
        if (code != 0) throw new XtoolException(run.Log);
    }

    public static async Task LogoutAsync(CancellationToken ct)
    {
        using var run = XtoolRun.Start("auth", "logout");
        await run.ReadAsync(_ => Task.CompletedTask, ct);
    }

    // Signs and installs. stage: what it's doing and how far overall, 0..1. message: what it asks of the phone. confirm: whether a
    // certificate may be replaced, which is the person's to say (another app signed with it would stop working).
    public static async Task InstallAsync(string udid, string ipa, Action<string?, double> stage, Action<PhoneRequest> phone, Func<Task<bool>> confirm, CancellationToken ct)
    {
        using var relay = await StartRelayAsync(ct);
        string path = OperatingSystem.IsWindows() ? Setup.ToLinuxPath(ipa) : ipa;
        using var run = XtoolRun.Start("install", "--usb", "--udid", udid, path);
        double furthest = 0;
        int code = await run.ReadAsync(async e =>
        {
            switch (e.Kind)
            {
                case OutKind.Stage:
                    var (friendly, overall) = Progress.Of(e.Text, e.Percent);
                    furthest = Math.Max(furthest, overall);
                    stage(friendly, furthest);
                    break;
                case OutKind.Line when e.Text.StartsWith("Please tap 'trust'"): phone(PhoneRequest.Trust); break;
                case OutKind.Line when e.Text.StartsWith("Please unlock"): phone(PhoneRequest.Unlock); break;
                case OutKind.Line when e.Text.StartsWith("Continuing..."): phone(PhoneRequest.None); break;
                case OutKind.Prompt when e.Prompt == PromptKind.Confirm:
                    await run.SendAsync(await confirm() ? "yes" : "no");
                    break;
            }
        }, ct);
        if (code != 0) throw new XtoolException(run.Log);
    }

    // The way to the phone on Windows (see Relay). Closing its stdin ends it, also if this program dies.
    private static async Task<IDisposable> StartRelayAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return new Nothing();
        string exe = RelayExe();
        var psi = new ProcessStartInfo("wsl.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in Setup.WslArgs("perl", "/opt/ump/relay.pl", Setup.ToLinuxPath(exe))) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new IOException("relay");
        _ = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            string? line = await p.StandardOutput.ReadLineAsync(timeout.Token);
            if (line?.Trim() != "ready") throw new IOException("The way to the iPhone didn't open");
        }
        catch
        {
            p.Kill(true);
            p.Dispose();
            throw;
        }
        return new RelayProcess(p);
    }

    // Linux sees only the drives Windows calls fixed: run from a USB stick or a network folder, it needs a copy on this disk.
    private static string RelayExe()
    {
        string exe = Environment.ProcessPath ?? throw new IOException("no path");
        try
        {
            if (new DriveInfo(exe).DriveType == DriveType.Fixed) return exe;
        }
        catch { }
        string copy = Path.Combine(Setup.DataDir, Path.GetFileName(exe));
        Directory.CreateDirectory(Setup.DataDir);
        File.Copy(exe, copy, true);
        return copy;
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class RelayProcess(Process process) : IDisposable
    {
        public void Dispose()
        {
            try { process.Kill(true); }
            catch { }
            process.Dispose();
        }
    }
}

public enum PhoneRequest { None, Unlock, Trust }

// xtool's stages as a person would say them, and how much of the whole each one is (rough shares, from a real install: the
// upload and Apple's provisioning take the most).
public static class Progress
{
    private static readonly (string Stage, string Friendly, double From, double To)[] Stages =
    {
        ("Unpacking app", "Getting the app ready", 0.00, 0.06),
        ("Preparing device", "Getting your iPhone ready", 0.06, 0.12),
        ("Provisioning", "Apple is preparing the signature", 0.12, 0.30),
        ("Signing", "Signing the app with your Apple ID", 0.30, 0.42),
        ("Packaging", "Packing it up", 0.42, 0.50),
        ("Connecting", "Connecting to your iPhone", 0.50, 0.54),
        ("Uploading", "Sending it to your iPhone", 0.54, 0.82),
        ("Installing", "Your iPhone is installing it", 0.82, 1.00),
    };

    public static (string? Friendly, double Overall) Of(string stage, double? percent)
    {
        foreach (var s in Stages)
            if (stage.StartsWith(s.Stage, StringComparison.OrdinalIgnoreCase))
                return (L.T(s.Friendly), s.From + (s.To - s.From) * (percent ?? 0));
        return (null, 0);
    }
}

// What went wrong, in words a person can act on. The raw text stays under "Details".
public static class Errors
{
    // Other: a second way out, with its own words; it signs out and starts again.
    public sealed record Advice(string Title, string Body, string? Other = null);

    private const string AnotherId = "Use another Apple ID", SignInAgain = "Sign in again";

    private static readonly (string[] Marks, string Title, string Body, string? Other)[] Known =
    {
        (new[] { "-20101", "-22406", "incorrect", "invalid credentials" }, "Wrong email or password", "Check them and try again: they're the ones you use for iCloud.", null),
        (new[] { "-21669", "-22951", "incorrect verification" }, "That code didn't work", "Ask for a new one and type it right away.", null),
        (new[] { "-80045", "-80046", "-36607", "HTTP 503", "cannot be completed at this time" }, "Apple is refusing the login right now", "It happens: Apple changes its rules every now and then. Try again in a little while, and look for a new version of this program.", null),
        (new[] { "0xe8008024", "0xe8008018", "profile is banned", "identity no longer valid" }, "Apple rejected this Apple ID on this iPhone", "It's tied to the account, not to you or the app. A different free Apple ID usually works.", AnotherId),
        (new[] { "session has expired", "expired token", "Please log in" }, "Apple wants you to sign in again", "It happens once in a while. Sign in again and it carries on from here.", SignInAgain),
        (new[] { "deadlineExceeded", "connectTimeout", "anisette", "NIOConnection", "notConnectedToInternet", "nameResolution" }, "I can't reach the signing service", "Check your connection. If it's fine, the service is down for a moment: try again in a few minutes.", null),
        (new[] { "No such host", "HttpRequestException", "Name or service not known", "SSL connection", "response status code" }, "No internet connection", "I can't reach GitHub to get the app. Check your connection and try again.", null),
        (new[] { "maximum number", "too many", "App ID limit" }, "This Apple ID reached Apple's free limit", "A free account holds 3 apps at a time and 10 new ones a week. Remove a sideloaded app from your iPhone or use a different Apple ID.", AnotherId),
        (new[] { "AFCClient", "muxError", "Device not found", "no device", "kAMDMobileImageMounter" }, "I lost your iPhone", "Check the cable, unlock the phone and try again.", null),
    };

    public static Advice For(string details)
    {
        foreach (var k in Known)
            if (k.Marks.Any(m => details.Contains(m, StringComparison.OrdinalIgnoreCase)))
                return new Advice(L.T(k.Title), L.T(k.Body), k.Other == null ? null : L.T(k.Other));
        return new Advice(L.T("Something didn't work"), L.T("Try again. If it keeps happening, copy the details and open an issue on GitHub."));
    }
}
