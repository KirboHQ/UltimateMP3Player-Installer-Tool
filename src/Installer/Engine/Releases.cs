using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Installer;

public sealed record IpaRelease(string Version, string Url, long Size, string? Sha256);

public static class Releases
{
    private const string AppRepo = "KirboHQ/UltimateMP3Player";
    private const string ToolRepo = "KirboHQ/UltimateMP3Player-Installer-Tool";
    private const string IpaName = "UltimateMP3Player-ios.ipa";

    // The page with the pictures and a big download button, not the list of files of a release: it's what a friend gets.
    public const string ShareLink = "https://github.com/" + ToolRepo;
    public const string IssuesLink = "https://github.com/" + ToolRepo + "/issues/new";

    public static readonly string ToolVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.2";

    // The newest release that has its .ipa: it comes about 10 minutes after the release itself, from the Mac that builds it, so in
    // those minutes "latest" alone would find nothing.
    public static async Task<IpaRelease?> LatestIpaAsync(CancellationToken ct)
    {
        try
        {
            var json = await GetAsync($"https://api.github.com/repos/{AppRepo}/releases?per_page=6", ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                {
                    if (asset.GetProperty("name").GetString() != IpaName) continue;
                    string? sha = asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:") ? digest[7..] : null;
                    return new IpaRelease(Clean(release.GetProperty("tag_name").GetString()), asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64(), sha);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return await FromRedirectAsync(ct);
    }

    // GitHub's API allows 60 calls an hour per address, and many share one: its plain pages have no such limit.
    private static async Task<IpaRelease?> FromRedirectAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, $"https://github.com/{AppRepo}/releases/latest");
            using var response = await Setup.Http.SendAsync(request, ct);
            string? path = response.RequestMessage?.RequestUri?.AbsolutePath;
            int at = path?.LastIndexOf("/tag/", StringComparison.Ordinal) ?? -1;
            if (at < 0) return null;
            string tag = path![(at + 5)..];
            return new IpaRelease(Clean(tag), $"https://github.com/{AppRepo}/releases/download/{tag}/{IpaName}", 0, null);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static async Task<string> FetchIpaAsync(IpaRelease release, Action<double> progress, CancellationToken ct)
    {
        string dir = Path.Combine(Setup.DataDir, "app");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"UltimateMP3Player-{release.Version}.ipa");
        await Setup.DownloadAsync(release.Url, path, release.Sha256, progress, ct);
        // Only the newest one stays: the old ones would pile up, 40 MB each.
        foreach (var old in Directory.GetFiles(dir))
            if (old != path) File.Delete(old);
        return path;
    }

    // A newer version of this program, or null. When Apple changes its rules, a new one here is the fix.
    public static async Task<string?> NewerToolAsync(CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await GetAsync($"https://api.github.com/repos/{ToolRepo}/releases/latest", ct));
            string latest = Clean(doc.RootElement.GetProperty("tag_name").GetString());
            return IsNewer(latest, ToolVersion) ? latest : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static bool IsNewer(string a, string b) => Version.TryParse(a, out var x) && Version.TryParse(b, out var y) && x > y;

    private static string Clean(string? tag) => (tag ?? "").TrimStart('v', 'V');

    private static async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Setup.Http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeout.Token);
    }
}

// What's remembered between runs: which phone has which version since when (iOS doesn't say it without much more talking to it),
// and whether the computer is ready, so that the window opens at the right place without starting WSL first.
public sealed class Memory
{
    public sealed class PhoneRecord
    {
        public string Version { get; set; } = "";
        public DateTime InstalledUtc { get; set; }
    }

    public Dictionary<string, PhoneRecord> Phones { get; set; } = new();
    public string? AppleId { get; set; }
    public bool EngineReady { get; set; }

    // The computer is restarting for WSL: when this program reopens by itself, it goes on instead of starting over.
    public bool ResumeSetup { get; set; }

    private static string File_ => Path.Combine(Setup.DataDir, "memory.json");

    public static Memory Load()
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(File_), MemoryJson.Default.Memory) ?? new Memory(); }
        catch { return new Memory(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Setup.DataDir);
            string tmp = File_ + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, MemoryJson.Default.Memory));
            File.Move(tmp, File_, true);
        }
        catch { }
    }

    public const int SignedDays = 7;
}

[JsonSerializable(typeof(Memory))]
internal sealed partial class MemoryJson : JsonSerializerContext;
