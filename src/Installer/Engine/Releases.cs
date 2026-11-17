using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Installer;

public sealed record IpaRelease(string Version, string Url, long Size, string? Sha256);
public sealed record ToolRelease(string Version, string? Url, string? Sha256);

public static class Releases
{
    private const string AppRepo = "KirboHQ/UltimateMP3Player";
    private const string ToolRepo = "KirboHQ/UltimateMP3Player-Installer-Tool";
    private const string IpaName = "UltimateMP3Player-ios.ipa";

    // The page with the pictures and a big download button, not the list of files of a release: it's what a friend gets.
    public const string ShareLink = "https://github.com/" + ToolRepo;
    public const string IssuesLink = "https://github.com/" + ToolRepo + "/issues/new";
    // (its line for this is "iCloud Account & Sign In", in Italian "Account e Accesso iCloud")
    public static string AppleStatusLink => L.Italian ? "https://www.apple.com/it/support/systemstatus/" : "https://www.apple.com/support/systemstatus/";

    public static readonly string ToolVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0";

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

    // ---- SideStore, for the phones that renew the app by themselves

    // Pinned, with its hash, like everything this program hands the Apple ID to. 0.7.0-alpha: 0.6.4 and older can't sign in
    // since Apple changed its servers (September 2026).
    public const string SideStoreVersion = "0.7.0-alpha";
    private const string SideStoreUrl = "https://github.com/SideStore/SideStore/releases/download/" + SideStoreVersion + "/SideStore.ipa";
    private const string SideStoreSha256 = "e334f86e6ceeab2e0d886c07611246533f474bb18838cf13ebc09ed1f0d622b5";

    // What the phone's camera opens from the codes on the screen: LocalDevVPN in the App Store (SideStore reaches the phone
    // through it), and the app's own source added to SideStore, which installs it and keeps it renewed and updated from there.
    public const string LocalDevVpnLink = "https://apps.apple.com/app/id6755608044";
    public const string SideStoreSourceLink = "sidestore://source?url=https%3A%2F%2Fgithub.com%2FKirboHQ%2FUltimateMP3Player%2Freleases%2Flatest%2Fdownload%2Fsidestore-source.json";

    public static async Task<string> FetchSideStoreAsync(Action<double> progress, CancellationToken ct)
    {
        string dir = Path.Combine(Setup.DataDir, "sidestore");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"SideStore-{SideStoreVersion}.ipa");
        await Setup.DownloadAsync(SideStoreUrl, path, SideStoreSha256, progress, ct);
        foreach (var old in Directory.GetFiles(dir))
            if (old != path) File.Delete(old);
        return path;
    }

    // A newer version of this program, or null, with this system's package from it when it's there (SelfUpdate puts it in
    // place). When Apple changes its rules, a new one here is the fix.
    public static async Task<ToolRelease?> NewerToolAsync(CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await GetAsync($"https://api.github.com/repos/{ToolRepo}/releases/latest", ct));
            string latest = Clean(doc.RootElement.GetProperty("tag_name").GetString());
            if (!IsNewer(latest, ToolVersion)) return null;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != SelfUpdate.Asset) continue;
                string? sha = asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:") ? digest[7..] : null;
                return new ToolRelease(latest, asset.GetProperty("browser_download_url").GetString(), sha);
            }
            return new ToolRelease(latest, null, null);
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
// and what's left to tidy up after versions before 1.1.
public sealed class Memory
{
    public sealed class PhoneRecord
    {
        public string Version { get; set; } = "";
        public DateTime InstalledUtc { get; set; }

        // Installed by the helper, under the bundle id that ends with the team: false for a phone last done with xtool, whose copy
        // of the app still has to give its data to the new one (Helper.MoveFromOldAppAsync).
        public bool ByHelper { get; set; }

        // SideStore is on it and renews the app by itself: the date above is only when it was last done from here.
        public bool SideStore { get; set; }
    }

    public Dictionary<string, PhoneRecord> Phones { get; set; } = new();
    public string? AppleId { get; set; }

    // Setup.RemoveOldEngineAsync has run (it's tried again at each start until it works).
    public bool OldEngineRemoved { get; set; }

    // WSL was left empty by it, and the person hasn't said yet whether it goes too.
    public bool WslLeftover { get; set; }

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
