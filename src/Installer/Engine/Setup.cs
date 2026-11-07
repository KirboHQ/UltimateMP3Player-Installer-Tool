using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;

namespace Installer;

// What stops the setup until the person does something: each one has its own words and its own button on the screen.
public enum Blocker { None, AppleDevices, Usbmuxd, ZipTools, WslConsent, Reboot, Virtualization, OldWindows }

public sealed class BlockedException(Blocker blocker, string detail = "") : Exception(detail)
{
    public Blocker Blocker { get; } = blocker;
}

// Gets the computer ready for xtool: on Windows a Linux of our own inside WSL (nothing else of the person's WSL is touched, and
// removing it takes one command), on a Mac and on Linux xtool's own build in the app's folder.
public static class Setup
{
    public const string Distro = "UltimateMP3Player-Installer";

    // Pinned with their hashes: this program hands them an Apple password, so nothing is ever fetched "latest" here.
    private const string XtoolVersion = "1.24.0";
    private const string XtoolBase = "https://github.com/xtool-org/xtool/releases/download/" + XtoolVersion + "/";
    private const string UbuntuBase = "https://cdimage.ubuntu.com/ubuntu-base/releases/24.04/release/";

    private static readonly bool Arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;

    private static (string Url, string Sha256) XtoolAsset => OperatingSystem.IsMacOS()
        ? (XtoolBase + "xtool.app.zip", "a56e98d69dea02a97a17e609f0ae8771c57ffaab23f1d62513fd9ada222026b3")
        : Arm
            ? (XtoolBase + "xtool-aarch64.AppImage", "4bc0515fed71efb86e82564f358efa0c4c6d76bf1bcfd4c5f256727b53ec59c8")
            : (XtoolBase + "xtool-x86_64.AppImage", "dbfd9d7216f077c84c171b688c4173c37c23ca76bf7fd185d1a9ae8a53dab59f");

    private static (string Url, string Sha256) LinuxAsset => Arm
        ? (UbuntuBase + "ubuntu-base-24.04.5-base-arm64.tar.gz", "a91d5a93010193712d346d761372b7c9db6dfcf093893161c64ca107f05914f2")
        : (UbuntuBase + "ubuntu-base-24.04.5-base-amd64.tar.gz", "e77b6f10c2590cef872b33ee9f635a0e3fd1f57fb074c0e52b5c7f56147a0c86");

    // xtool runs these two by name, to open the .ipa and to close it again once signed, and the base has neither. The release's own
    // files: they stay in Ubuntu's pool for as long as the release does, while those of its updates get replaced. What they need is in the base.
    private static (string Url, string Sha256)[] ZipPackages => Arm
        ? new[]
        {
            ("https://ports.ubuntu.com/ubuntu-ports/pool/main/z/zip/zip_3.0-13build1_arm64.deb", "7b5af36fbf287221ea21aa08817f139b6ef463c07aeb2dc0e4233d2272b0c64c"),
            ("https://ports.ubuntu.com/ubuntu-ports/pool/main/u/unzip/unzip_6.0-28ubuntu4_arm64.deb", "d86487038933474265b128366cea64e61e0b99471aa7499ea0173411831736aa"),
        }
        : new[]
        {
            ("https://archive.ubuntu.com/ubuntu/pool/main/z/zip/zip_3.0-13build1_amd64.deb", "5c1a2dfb052f50e7031037aa2e267839804d2254c4d282040fdfe826ffba1e38"),
            ("https://archive.ubuntu.com/ubuntu/pool/main/u/unzip/unzip_6.0-28ubuntu4_amd64.deb", "9ea37bc2f1e43df0a98530d08e5bab1a8801bdaf0a4c81330861e788d9155bbc"),
        };

    public static string DataDir { get; } = Environment.GetEnvironmentVariable("UMP_INSTALLER_DATA") is { Length: > 0 } test
        ? test
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ultimate MP3 Player Installer");

    private static string InData(string name)
    {
        Directory.CreateDirectory(DataDir);
        return Path.Combine(DataDir, name);
    }

    public static readonly HttpClient Http = MakeHttp();

    private static HttpClient MakeHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("UltimateMP3PlayerInstaller");
        return http;
    }

    // ---- Running things

    public static async Task<(int Code, string Output)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct, string? input = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
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
        if (input != null) await p.StandardInput.WriteAsync(input);
        p.StandardInput.Close();
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return (p.ExitCode, (await output + await errors).Trim());
    }

    // A command in our Linux, as root (it's nobody's but ours). -e: without a shell in between, the arguments stay as they are.
    // Without \r: a script in a raw string has the line endings of the checkout, CRLF on the Windows that builds the release, and sh can't read those.
    public static IEnumerable<string> WslArgs(params string[] command)
        => new[] { "-d", Distro, "-u", "root", "--cd", "/", "-e" }.Concat(command.Select(c => c.Replace("\r", "")));

    private static Task<(int Code, string Output)> InLinuxAsync(CancellationToken ct, string script, string? input = null, params string[] args)
        => RunAsync("wsl.exe", WslArgs(new[] { "sh", "-c", script, "sh" }.Concat(args).ToArray()), ct, input);

    public static string ToLinuxPath(string windowsPath)
        => "/mnt/" + char.ToLowerInvariant(windowsPath[0]) + windowsPath[2..].Replace('\\', '/');

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

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { }
    }

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

    // ---- WSL

    public static async Task<bool> WslPresentAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return true;
        // (WSL 2 itself asks for 19041; older builds have no "wsl --install".)
        if (Environment.OSVersion.Version.Build < 19041) throw new BlockedException(Blocker.OldWindows);
        try { return (await RunAsync("wsl.exe", new[] { "--status" }, ct)).Code == 0; }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    // Windows' own permission window comes up here, the only time this program asks to be an administrator.
    public static async Task InstallWslAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("wsl.exe", "--install --no-distribution")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { throw new BlockedException(Blocker.WslConsent); }
        if (p == null) throw new IOException("wsl --install");
        using (p) await p.WaitForExitAsync(ct);
    }

    // Asks to come back by itself after the restart WSL needs.
    public static void RestartComputerAndResume()
    {
        if (OperatingSystem.IsWindows() && Environment.ProcessPath is { } exe)
        {
            using var runOnce = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce");
            runOnce.SetValue("UltimateMP3PlayerInstaller", $"\"{exe}\"");
        }
        try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true }); }
        catch { }
    }

    private static Exception WslFailure(string output)
    {
        string o = output.ToUpperInvariant();
        if (o.Contains("0X80370102") || o.Contains("HCS_E_HYPERV_NOT_INSTALLED") || o.Contains("VIRTUALIZATION") || o.Contains("VIRTUALIZZAZIONE"))
            return new BlockedException(Blocker.Virtualization, output);
        if (o.Contains("WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED") || o.Contains("REBOOT") || o.Contains("RESTART") || o.Contains("RIAVVI") || o.Contains("REGDB_E_CLASSNOTREG"))
            return new BlockedException(Blocker.Reboot, output);
        return new IOException(output);
    }

    // ---- xtool

    public static string ExtractedApp => InData(Path.Combine("xtool", "AppRun"));
    private static string MacApp => InData(Path.Combine("xtool", "xtool.app", "Contents", "MacOS", "xtool"));
    private static string VersionFile => InData("engine-version");

    public static string NativeXtool => OperatingSystem.IsMacOS() ? MacApp : ExtractedApp;

    public static async Task<bool> EngineReadyAsync(CancellationToken ct)
    {
        // (a Mac always has them; on Linux they're the person's system's, and many have only one or none)
        if (OperatingSystem.IsLinux() && !HasZipTools()) throw new BlockedException(Blocker.ZipTools);
        if (!OperatingSystem.IsWindows())
            return File.Exists(NativeXtool) && File.Exists(VersionFile) && (await File.ReadAllTextAsync(VersionFile, ct)).Trim() == XtoolVersion;
        if (!await WslPresentAsync(ct) || !await DistroPresentAsync(ct)) return false;
        // (the two tools as well: a private Linux can have the right xtool without them, if it was set up before they went in)
        var (code, output) = await InLinuxAsync(ct, "[ -x /usr/bin/zip ] && [ -x /usr/bin/unzip ] && cat /opt/ump/version");
        return code == 0 && output.Trim() == XtoolVersion;
    }

    public static bool HasZipTools(string? path = null)
    {
        var dirs = (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        return new[] { "zip", "unzip" }.All(tool => dirs.Any(dir => File.Exists(Path.Combine(dir, tool))));
    }

    private static async Task<bool> DistroPresentAsync(CancellationToken ct)
    {
        var (code, output) = await RunAsync("wsl.exe", new[] { "-l", "-q" }, ct);
        return code == 0 && output.Split('\n').Any(l => l.Trim() == Distro);
    }

    // progress(0..1) for what can be measured, null for what can't; a line saying what's going on comes with it.
    public static async Task PrepareEngineAsync(Action<double?, string> progress, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) await PrepareLinuxAsync(progress, ct);
        else await PrepareNativeAsync(progress, ct);
    }

    private static async Task PrepareNativeAsync(Action<double?, string> progress, CancellationToken ct)
    {
        var (url, sha) = XtoolAsset;
        string file = InData(Path.GetFileName(url)), dir = InData("xtool");
        await DownloadAsync(url, file, sha, p => progress(p, L.T("Downloading the signing engine")), ct);
        progress(null, L.T("Unpacking it"));
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsMacOS())
        {
            // (ditto keeps the app's signature and links, what ZipFile wouldn't)
            var (code, output) = await RunAsync("/usr/bin/ditto", new[] { "-x", "-k", file, dir }, ct);
            if (code != 0) throw new IOException(output);
        }
        else
        {
            // Extracted, so that it doesn't need FUSE, which many systems no longer have.
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var psi = new ProcessStartInfo(file, "--appimage-extract") { WorkingDirectory = DataDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi) ?? throw new IOException(file);
            _ = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _ = p.StandardError.ReadToEndAsync(CancellationToken.None);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) throw new IOException("xtool could not be unpacked");
            Directory.Delete(dir, true);
            Directory.Move(Path.Combine(DataDir, "squashfs-root"), dir);
        }
        File.Delete(file);
        await File.WriteAllTextAsync(VersionFile, XtoolVersion, ct);
    }

    private static async Task PrepareLinuxAsync(Action<double?, string> progress, CancellationToken ct)
    {
        if (!await DistroPresentAsync(ct))
        {
            var (url, sha) = LinuxAsset;
            string gz = InData("linux.tar.gz"), tar = InData("linux.tar"), home = InData("linux");
            await DownloadAsync(url, gz, sha, p => progress(p, L.T("Downloading the private Linux")), ct);
            progress(null, L.T("Setting up the private Linux"));
            // Unpacked first: only newer versions of wsl.exe read a .gz.
            await Task.Run(async () =>
            {
                await using var from = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress);
                await using var to = File.Create(tar);
                await from.CopyToAsync(to, ct);
            }, ct);
            var (code, output) = await RunAsync("wsl.exe", new[] { "--import", Distro, home, tar, "--version", "2" }, ct);
            File.Delete(tar);
            if (code != 0) throw WslFailure(output);
            File.Delete(gz);
        }

        var (xtoolUrl, xtoolSha) = XtoolAsset;
        string image = InData("xtool.AppImage");
        await DownloadAsync(xtoolUrl, image, xtoolSha, p => progress(p, L.T("Downloading the signing engine")), ct);
        progress(null, L.T("Unpacking the signing engine"));
        var (done, log) = await InLinuxAsync(ct, """
            set -e
            rm -rf /opt/ump/next
            mkdir -p /opt/ump/next
            cd /opt/ump/next
            cp "$1" ./xtool.AppImage
            chmod +x ./xtool.AppImage
            ./xtool.AppImage --appimage-extract > /dev/null
            rm ./xtool.AppImage
            rm -rf /opt/ump/xtool
            mv squashfs-root /opt/ump/xtool
            cd /
            rmdir /opt/ump/next
            """, null, ToLinuxPath(image));
        if (done != 0) throw WslFailure(log);
        File.Delete(image);

        progress(null, L.T("Finishing up"));
        var debs = new List<string>();
        foreach (var (url, sha) in ZipPackages)
        {
            debs.Add(InData(Path.GetFileName(url)));
            await DownloadAsync(url, debs[^1], sha, _ => { }, ct);
        }
        (done, log) = await InLinuxAsync(ct, "dpkg -i \"$@\" > /dev/null", null, debs.Select(ToLinuxPath).ToArray());
        if (done != 0) throw WslFailure(log);
        debs.ForEach(File.Delete);
        (done, log) = await InLinuxAsync(ct, "mkdir -p /etc/ssl/certs && cat > /etc/ssl/certs/ca-certificates.crt", SystemRoots());
        if (done != 0) throw WslFailure(log);
        (done, log) = await InLinuxAsync(ct, "cat > /opt/ump/relay.pl", Relay.Script.Replace("\r", ""));
        if (done != 0) throw WslFailure(log);
        (done, log) = await InLinuxAsync(ct, $"/opt/ump/xtool/AppRun --version > /dev/null && echo {XtoolVersion} > /opt/ump/version");
        if (done != 0) throw WslFailure(log);
    }

    // The minimal Linux comes without the certificates to trust: Windows' own go in, those every site is already checked against
    // here (a company's or an antivirus' root too, without which nothing would connect behind them).
    private static string SystemRoots()
    {
        var pem = new StringBuilder();
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadOnly);
            foreach (var c in store.Certificates) pem.Append(c.ExportCertificatePem()).Append('\n');
        }
        return pem.ToString();
    }

    public static async Task RemoveEverythingAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows() && await WslPresentAsync(ct) && await DistroPresentAsync(ct))
            await RunAsync("wsl.exe", new[] { "--unregister", Distro }, ct);
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
