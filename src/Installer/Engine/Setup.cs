using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Installer;

// What stops the setup until the person does something: each one has its own words and its own button on the screen.
public enum Blocker { None, AppleDevices, Usbmuxd }

public sealed class BlockedException(Blocker blocker, string detail = "") : Exception(detail)
{
    public Blocker Blocker { get; } = blocker;
}

// What the computer needs: Apple's service that talks to the phone (Apple Devices on Windows, usbmuxd on Linux, built into a
// Mac). The helper that signs comes with this program; what versions before 1.1 put here for xtool is taken away.
public static class Setup
{
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("UMP_INSTALLER_DATA") is { Length: > 0 } test
        ? test
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ultimate MP3 Player Installer");

    public static readonly HttpClient Http = MakeHttp();

    private static HttpClient MakeHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("UltimateMP3PlayerInstaller");
        return http;
    }

    public static async Task<(int Code, string Output)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // (or wsl.exe writes in UTF-16)
        psi.Environment["WSL_UTF8"] = "1";
        using var p = Process.Start(psi) ?? throw new IOException(exe);
        using var kill = ct.Register(() => { try { p.Kill(true); } catch { } });
        var output = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = p.StandardError.ReadToEndAsync(CancellationToken.None);
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return (p.ExitCode, (await output + await errors).Trim());
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { }
    }

    // ---- Apple's part: the service that talks to the phone

    private const string AppleDevicesFamily = "AppleInc.AppleDevices_nzyj5cx40ttqa";

    public static bool AppleDevicesInstalled()
    {
        if (!OperatingSystem.IsWindows()) return true;
        using var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        return packages?.GetSubKeyNames().Any(n => n.StartsWith("AppleInc.AppleDevices_", StringComparison.OrdinalIgnoreCase)) == true;
    }

    public static void OpenAppleDevicesInStore() => Open("ms-windows-store://pdp/?ProductId=9NP83LWLPZ9K");

    // Its service starts with the app (or at logon, if the person left that on).
    public static void StartAppleDevices() => Open($@"shell:AppsFolder\{AppleDevicesFamily}!App");

    // Waits for usbmuxd, starting Apple Devices when that's what brings it up. Returns when it answers; asks when it can't.
    public static async Task EnsurePhoneServiceAsync(CancellationToken ct)
    {
        if (await Usbmux.RunningAsync(ct)) return;
        if (OperatingSystem.IsMacOS()) throw new IOException("usbmuxd is not answering");
        if (OperatingSystem.IsLinux()) throw new BlockedException(Blocker.Usbmuxd);
        if (!AppleDevicesInstalled()) throw new BlockedException(Blocker.AppleDevices);
        StartAppleDevices();
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(500, ct);
            if (await Usbmux.RunningAsync(ct)) return;
        }
        throw new IOException("Apple Devices started, but its service isn't answering");
    }

    // ---- What versions before 1.1 left for xtool

    // The private Linux they made in WSL on Windows, with xtool and its sign-in inside.
    public const string OldDistro = "UltimateMP3Player-Installer";

    // What this version keeps in its folder: everything else in there was the old engine's (xtool, the Linux, their downloads).
    private static readonly string[] Kept = { "app", "helper", "sidestore", "memory.json", "memory.json.tmp" };

    // Takes away all of it. Returns true when WSL is left empty by it: then it was most likely there only for this program, and
    // the person is asked whether it goes too (RemoveWslAsync); with anything else in it, it isn't this program's to touch.
    public static async Task<bool> RemoveOldEngineAsync(CancellationToken ct)
    {
        bool wslEmptied = false;
        if (OperatingSystem.IsWindows())
        {
            // (the way back after the restart WSL needed, in case that never happened)
            using (var runOnce = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", true))
                runOnce?.DeleteValue("UltimateMP3PlayerInstaller", false);
            if ((await WslDistrosAsync(ct))?.Contains(OldDistro) == true)
            {
                var (code, output) = await RunAsync("wsl.exe", new[] { "--unregister", OldDistro }, ct);
                if (code != 0) throw new IOException(output);
                wslEmptied = (await WslDistrosAsync(ct))?.Count == 0;
            }
        }
        else if (!OnPath("xtool"))
        {
            // xtool's own sign-in and cache in the home folder, left alone when the person has an xtool of their own.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var dir in new[] { Path.Combine(home, ".config", "xtool"), Path.Combine(home, ".cache", "xtool") })
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        if (Directory.Exists(DataDir))
            foreach (var entry in Directory.EnumerateFileSystemEntries(DataDir))
                if (!Kept.Contains(Path.GetFileName(entry)))
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, true);
                    else File.Delete(entry);
                }
        return wslEmptied;
    }

    // The distributions in WSL, or null when there's no WSL at all ("wsl -l" fails also when it's there but empty).
    private static async Task<List<string>?> WslDistrosAsync(CancellationToken ct)
    {
        try
        {
            if ((await RunAsync("wsl.exe", new[] { "--status" }, ct)).Code != 0) return null;
            var (code, output) = await RunAsync("wsl.exe", new[] { "-l", "-q" }, ct);
            return code == 0 ? output.Split('\n').Select(l => l.Trim('\r', ' ', '\0')).Where(l => l.Length > 0).ToList() : new List<string>();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // WSL and the Virtual Machine Platform, as "wsl --install" turned them on, off again: Windows' permission window comes up, and
    // they're gone at the next restart. False when the person said no to the permission.
    public static async Task<bool> RemoveWslAsync(CancellationToken ct)
    {
        const string script = "wsl.exe --uninstall & dism.exe /online /norestart /quiet /disable-feature /featurename:VirtualMachinePlatform & dism.exe /online /norestart /quiet /disable-feature /featurename:Microsoft-Windows-Subsystem-Linux";
        var psi = new ProcessStartInfo("cmd.exe", "/c " + script) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { return false; }
        if (p == null) return false;
        using (p) await p.WaitForExitAsync(ct);
        return true;
    }

    private static bool OnPath(string tool)
        => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, tool)));

    // "Remove everything" and the uninstaller: the sign-in, the downloads, and the old engine if it's still there.
    public static async Task RemoveEverythingAsync(CancellationToken ct)
    {
        await RemoveOldEngineAsync(ct);
        if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true);
    }

    // ---- Downloads

    // Into a .part, then checked against its hash: whatever is half or wrong never gets used. Already there and right = no download.
    public static async Task DownloadAsync(string url, string path, string? sha256, Action<double> progress, CancellationToken ct)
    {
        if (File.Exists(path) && (sha256 == null || await HashAsync(path, ct) == sha256)) return;
        string part = path + ".part";
        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0, got = 0;
            await using var from = await response.Content.ReadAsStreamAsync(ct);
            await using var to = File.Create(part);
            var buffer = new byte[1 << 17];
            long lastShown = 0;
            int n;
            while ((n = await from.ReadAsync(buffer, ct)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n), ct);
                got += n;
                // (a few times a second are enough for a bar that glides by itself)
                if (total > 0 && Stopwatch.GetTimestamp() - lastShown > Stopwatch.Frequency / 8)
                {
                    lastShown = Stopwatch.GetTimestamp();
                    progress((double)got / total);
                }
            }
        }
        progress(1);
        if (sha256 != null && await HashAsync(part, ct) != sha256)
        {
            File.Delete(part);
            throw new IOException("The download doesn't match its hash: " + Path.GetFileName(url));
        }
        File.Move(part, path, true);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
    }
}
