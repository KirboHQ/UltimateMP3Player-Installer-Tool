using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Installer;

public sealed class HelperException(string details) : Exception(details)
{
    public string Details { get; } = details;
}

public enum PhoneRequest { None, Unlock, Trust }

// One run of ump-helper (helper/ in this repository, beside this program in the packages): it says what happens one JSON
// object a line, and reads the answers it asks for one line each.
public sealed partial class HelperRun : IDisposable
{
    private readonly Process _process;
    private readonly List<string> _log = new();
    private string? _error;

    // (what a person may copy and post in a report: without who they are, which phone, or their folders)
    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+")]
    private static partial Regex Email();
    [GeneratedRegex(@"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{16}\b|\b[0-9A-Fa-f]{40}\b")]
    private static partial Regex Udid();

    public static string Exe => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ump-helper.exe" : "ump-helper");

    private HelperRun(Process process) => _process = process;

    public static HelperRun Start(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // (without a BOM, which would end up in front of the password; and UTF-8 whatever the console's code page)
            StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("--data");
        psi.ArgumentList.Add(Path.Combine(Setup.DataDir, "helper"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        return new HelperRun(Process.Start(psi) ?? throw new IOException("ump-helper"));
    }

    // Reads it to the end, calling back (on the UI thread if that's where it was called from) for each event. Returns the exit code.
    public async Task<int> ReadAsync(Func<HelperEvent, Task> on, CancellationToken ct)
    {
        using var kill = ct.Register(Kill);
        var errors = Task.Run(async () =>
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync()) != null)
                lock (_log) _log.Add(line);
        });
        string? text;
        while ((text = await _process.StandardOutput.ReadLineAsync(CancellationToken.None)) != null)
        {
            if (HelperEvent.Parse(text) is not { } e) continue;
            if (e.Event == "error") _error = e.Text("message");
            try { await on(e); }
            catch
            {
                // (ended here, or it would wait for an answer that never comes)
                Kill();
                throw;
            }
        }
        await errors;
        await _process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return _process.ExitCode;
    }

    public async Task SendAsync(string line)
    {
        await _process.StandardInput.WriteLineAsync(line);
        await _process.StandardInput.FlushAsync();
    }

    // What went wrong and the last things the libraries said, for "Details".
    public string Log
    {
        get
        {
            string all;
            lock (_log) all = string.Join('\n', _log.TakeLast(30).Prepend(_error ?? "").Where(l => l.Length > 0));
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0) all = all.Replace(home, "~");
            return Email().Replace(Udid().Replace(all, "<iPhone>"), "<email>");
        }
    }

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

// One line from the helper: {"event": "stage", "stage": "Signing", "progress": 0.4} and the like.
public sealed class HelperEvent
{
    private readonly JsonElement _root;

    private HelperEvent(JsonElement root) => _root = root;

    public string Event => Text("event") ?? "";

    public static HelperEvent? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? new HelperEvent(doc.RootElement.Clone()) : null;
        }
        catch (JsonException) { return null; }
    }

    public string? Text(string name) => _root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public double? Number(string name) => _root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    public bool Flag(string name) => _root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public PhoneRequest Phone => Text("what") switch { "trust" => PhoneRequest.Trust, "unlock" => PhoneRequest.Unlock, _ => PhoneRequest.None };
}

public static class Helper
{
    // Who's signed in, or null.
    public static async Task<string?> AccountAsync(CancellationToken ct)
    {
        using var run = HelperRun.Start("status");
        string? account = null;
        await run.ReadAsync(e =>
        {
            if (e.Event == "account") account = e.Text("email");
            return Task.CompletedTask;
        }, ct);
        return account;
    }

    // askCode comes back with what the person typed, or null when they went back.
    public static async Task LoginAsync(string email, string password, Func<Task<string?>> askCode, CancellationToken ct)
    {
        using var run = HelperRun.Start("login", email);
        int code = await run.ReadAsync(async e =>
        {
            if (e.Event != "ask") return;
            switch (e.Text("what"))
            {
                case "password": await run.SendAsync(password); break;
                case "code":
                    var typed = await askCode();
                    if (typed == null) throw new OperationCanceledException();
                    await run.SendAsync(typed);
                    break;
            }
        }, ct);
        if (code != 0) throw new HelperException(run.Log);
    }

    public static async Task LogoutAsync(CancellationToken ct)
    {
        using var run = HelperRun.Start("logout");
        await run.ReadAsync(_ => Task.CompletedTask, ct);
    }

    // Signs and installs. stage: what it's doing and how far overall, 0..1. phone: what it asks of the phone. confirm: whether a
    // certificate may be replaced, which is the person's to say (another app signed with it would stop working).
    public static Task InstallAsync(string udid, string ipa, Action<string?, double> stage, Action<PhoneRequest> phone, Func<Task<bool>> confirm, CancellationToken ct)
        => SignAndInstallAsync(new[] { "install", udid, ipa }, stage, phone, confirm, ct);

    // SideStore, with what it needs to reach the phone by itself later (the pairing file); fresh: that made anew, to repair it.
    // True when it was on the phone already: false means its sign-in and its apps are gone, and the person's part starts over.
    public static Task<bool> InstallSideStoreAsync(string udid, string ipa, bool fresh, Action<string?, double> stage, Action<PhoneRequest> phone, Func<Task<bool>> confirm, CancellationToken ct)
        => SignAndInstallAsync(fresh ? new[] { "sidestore", udid, ipa, "fresh" } : new[] { "sidestore", udid, ipa }, stage, phone, confirm, ct);

    private static async Task<bool> SignAndInstallAsync(string[] args, Action<string?, double> stage, Action<PhoneRequest> phone, Func<Task<bool>> confirm, CancellationToken ct)
    {
        using var run = HelperRun.Start(args);
        double furthest = 0;
        bool existed = false;
        int code = await run.ReadAsync(async e =>
        {
            switch (e.Event)
            {
                case "stage":
                    var (friendly, overall) = Progress.Of(e.Text("stage") ?? "", e.Number("progress"));
                    // (a try again from the start doesn't take the bar back)
                    furthest = Math.Max(furthest, overall);
                    stage(friendly, furthest);
                    break;
                case "phone": phone(e.Phone); break;
                case "ask" when e.Text("what") == "revoke":
                    await run.SendAsync(await confirm() ? "yes" : "no");
                    break;
                case "done": existed = e.Flag("existed"); break;
            }
        }, ct);
        if (code != 0) throw new HelperException(run.Log);
        return existed;
    }

    // Versions before 1.1 installed the app with xtool, under another bundle id: to iOS another app, with the person's music in
    // it. Its data goes into the new one, then it goes. False when there was none.
    public static async Task<bool> MoveFromOldAppAsync(string udid, Action<double> progress, Action<PhoneRequest> phone, CancellationToken ct)
    {
        bool moved = false;
        using (var run = HelperRun.Start("move", udid))
        {
            int code = await run.ReadAsync(e =>
            {
                switch (e.Event)
                {
                    case "stage": progress(e.Number("progress") ?? 0); break;
                    case "phone": phone(e.Phone); break;
                    case "done": moved = e.Flag("moved"); break;
                }
                return Task.CompletedTask;
            }, ct);
            if (code != 0) throw new HelperException(run.Log);
        }
        if (!moved) return false;
        using (var run = HelperRun.Start("remove-old", udid))
        {
            if (await run.ReadAsync(_ => Task.CompletedTask, ct) != 0) throw new HelperException(run.Log);
        }
        return true;
    }
}

// The helper's stages as a person would say them, and how much of the whole each one is (from a real install: Apple's part
// and the upload take the most). Within Signing, Apple prepares the profile up to 0.3; within Installing, 0.7 is the upload.
public static class Progress
{
    public static (string? Friendly, double Overall) Of(string stage, double? percent)
    {
        double p = Math.Clamp(percent ?? 0, 0, 1);
        return stage switch
        {
            "Preparing" => (L.T("Getting your iPhone ready"), 0.10 * p),
            "Signing" => (L.T(p < 0.3 ? "Apple is preparing the signature" : "Signing the app with your Apple ID"), 0.10 + 0.35 * p),
            "Installing" => (L.T(p < 0.7 ? "Sending it to your iPhone" : "Your iPhone is installing it"), 0.45 + 0.55 * p),
            // (SideStore's way to the phone: made before its install, and put inside it after)
            "Pairing" => (L.T("Letting SideStore reach your iPhone by itself"), p < 0.5 ? 0 : 1),
            _ => (null, 0),
        };
    }
}

// What went wrong, in words a person can act on. The raw text stays under "Details".
public static class Errors
{
    // Other: a second way out, with its own words; it signs out and starts again.
    // AppleSide: it may well be Apple's sign-in being down for everyone (it happens), so the status page is offered.
    public sealed record Advice(string Title, string Body, string? Other = null, bool AppleSide = false);

    private const string AnotherId = "Use another Apple ID", SignInAgain = "Sign in again";

    private static readonly (string[] Marks, string Title, string Body, string? Other, bool AppleSide)[] Known =
    {
        (new[] { "-20101", "-22406", "-20970", "entered incorrectly", "incorrect password", "invalid credentials" }, "Wrong email or password", "Check them and try again: they're the ones you use for iCloud.", null, false),
        (new[] { "-21669", "-22951", "incorrect verification" }, "That code didn't work", "Ask for a new one and type it right away.", null, false),
        (new[] { "-80045", "-80046", "-36607", "HTTP 503", "503 Service", "cannot be completed at this time" }, "Apple is refusing the login right now", "It happens: Apple changes its rules every now and then. Try again in a little while, and look for a new version of this program.", null, true),
        (new[] { "0xe8008024", "0xe8008018", "profile is banned", "identity no longer valid" }, "Apple rejected this Apple ID on this iPhone", "It's tied to the account, not to you or the app. A different free Apple ID usually works.", AnotherId, false),
        (new[] { "session has expired", "expired token", "Please log in", "Not signed in" }, "Apple wants you to sign in again", "It happens once in a while. Sign in again and it carries on from here.", SignInAgain, false),
        (new[] { "Don't Trust", "UserDeniedPairing" }, "Your iPhone said Don't Trust", "Unplug it and plug it in again: when it asks, tap Trust and enter its passcode.", null, false),
        (new[] { "error sending request", "tcp connect error", "dns error", "anisette", "URL Bag" }, "I can't reach the signing service", "Check your connection. If it's fine, the service is down for a moment: try again in a few minutes.", null, true),
        (new[] { "No such host", "HttpRequestException", "Name or service not known", "SSL connection", "response status code" }, "No internet connection", "I can't reach GitHub to get the app. Check your connection and try again.", null, false),
        (new[] { "maximum number", "too many", "App ID limit" }, "This Apple ID reached Apple's free limit", "A free account holds 3 apps at a time and 10 new ones a week. Remove a sideloaded app from your iPhone or use a different Apple ID.", AnotherId, false),
        (new[] { "device not found", "device socket io failed", "no device", "AFC" }, "I lost your iPhone", "Check the cable, unlock the phone and try again.", null, false),
    };

    public static Advice For(string details)
    {
        foreach (var k in Known)
            if (k.Marks.Any(m => details.Contains(m, StringComparison.OrdinalIgnoreCase)))
                return new Advice(L.T(k.Title), L.T(k.Body), k.Other == null ? null : L.T(k.Other), k.AppleSide);
        return new Advice(L.T("Something didn't work"), L.T("Try again. If it keeps happening, copy the details and open an issue on GitHub."));
    }
}
