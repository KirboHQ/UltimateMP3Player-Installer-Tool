using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Installer;

// This program updating itself from its latest release, as Ultimate MP3 Player does: downloaded while it's in use, put in
// place when the window closes, or at once with a restart. Windows runs the new setup silently over the installed one, Linux
// swaps the files of its folder, macOS the .app. Only with GitHub's hash of the file: this program handles an Apple password,
// so nothing unchecked ever takes its place.
public static class SelfUpdate
{
    // The README's download buttons and release.yml use the same names.
    public static string Asset => OperatingSystem.IsWindows() ? "UltimateMP3Player-Installer-windows-x64-setup.exe"
        : OperatingSystem.IsMacOS() ? "UltimateMP3Player-Installer-macos-arm64.zip"
        : "UltimateMP3Player-Installer-linux-x64.tar.gz";

    private const string Program = "UltimateMP3PlayerInstaller";

    // The downloaded setup (Windows), or the new copy unpacked and waiting: the folder (Linux), the .app (macOS).
    private static string? _ready;

    private static string Exe => Environment.ProcessPath ?? "";
    private static string Folder => Path.GetDirectoryName(Exe) ?? "";

    // macOS: the .app around the running program (…/X.app/Contents/MacOS/program).
    private static string? Bundle => Path.GetDirectoryName(Path.GetDirectoryName(Folder)) is { } b && b.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? b : null;

    // Only a copy that came from a release, where it may write: the setup's (its uninstaller beside it), the .app, the
    // unpacked folder. A build from the sources never replaces itself.
    public static bool CanUpdate
    {
        get
        {
            if (Exe.Length == 0 || Exe.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) return false;
            if (OperatingSystem.IsWindows()) return File.Exists(Path.Combine(Folder, "unins000.exe"));
            if (OperatingSystem.IsMacOS()) return Bundle is { } b && Writable(Path.GetDirectoryName(b)!);
            return Path.GetFileName(Exe) == Program && Writable(Folder);
        }
    }

    private static bool Writable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".ump-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static string Scratch => Path.Combine(Path.GetTempPath(), "UltimateMP3Player-Installer-update");

    // macOS: beside the .app, so that the swap is a rename on the same disk.
    private static string Unpacked => OperatingSystem.IsMacOS() ? Path.Combine(Path.GetDirectoryName(Bundle!)!, ".ump-installer-update") : Path.Combine(Scratch, "new");

    // What a previous update left: the files it replaced, the download.
    public static void CleanUp()
    {
        if (!CanUpdate) return;
        try
        {
            if (OperatingSystem.IsMacOS() && Bundle is { } b)
                foreach (var old in Directory.EnumerateDirectories(Path.GetDirectoryName(b)!, ".ump-installer-old-*")) Directory.Delete(old, true);
            else if (!OperatingSystem.IsWindows() && Folder.Length > 0)
                foreach (var old in Directory.EnumerateFiles(Folder, "*.old", SearchOption.AllDirectories)) File.Delete(old);
        }
        catch { }
        try { if (Directory.Exists(Scratch)) Directory.Delete(Scratch, true); }
        catch { }
    }

    // Downloads it and gets it ready. False when it can't be done here, or without a hash.
    public static async Task<bool> PrepareAsync(ToolRelease release, CancellationToken ct)
    {
        if (!CanUpdate || release.Url == null || release.Sha256 == null) return false;
        try
        {
            Directory.CreateDirectory(Scratch);
            string file = Path.Combine(Scratch, Asset);
            await Setup.DownloadAsync(release.Url, file, release.Sha256, _ => { }, ct);
            if (OperatingSystem.IsWindows())
            {
                _ready = file;
                return true;
            }
            _ready = await Task.Run(() => Unpack(file), ct);
            File.Delete(file);
            return _ready != null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static string? Unpack(string archive)
    {
        string dir = Unpacked;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsMacOS())
        {
            ZipFile.ExtractToDirectory(archive, dir);
            string? app = Directory.EnumerateDirectories(dir, "*.app").FirstOrDefault();
            if (app == null || !File.Exists(Path.Combine(app, "Contents", "MacOS", Program))) return null;
            foreach (var exe in Directory.EnumerateFiles(Path.Combine(app, "Contents", "MacOS"))) MakeExecutable(exe);
            return app;
        }
        using (var file = File.OpenRead(archive))
        using (var gz = new GZipStream(file, CompressionMode.Decompress))
            TarFile.ExtractToDirectory(gz, dir, true);
        string? program = Directory.EnumerateFiles(dir, Program, SearchOption.AllDirectories).FirstOrDefault();
        if (program == null) return null;
        string folder = Path.GetDirectoryName(program)!;
        foreach (var exe in new[] { program, Path.Combine(folder, "ump-helper") })
            if (File.Exists(exe)) MakeExecutable(exe);
        return folder;
    }

    private static void MakeExecutable(string file)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute); }
        catch { }
    }

    // Puts it in place, once; with relaunch the new version opens right after. Called as the window closes.
    public static void Apply(bool relaunch)
    {
        string? ready = Interlocked.Exchange(ref _ready, null);
        if (ready == null) return;
        try
        {
            if (OperatingSystem.IsWindows()) RunSetup(ready, relaunch);
            else if (OperatingSystem.IsMacOS() ? SwapBundle(ready) : SwapFolder(ready))
            {
                if (relaunch)
                    Process.Start(OperatingSystem.IsMacOS()
                        ? new ProcessStartInfo("open", new[] { "-n", Bundle! }) { UseShellExecute = false }
                        : new ProcessStartInfo(Exe) { UseShellExecute = false });
            }
        }
        catch { }
    }

    // The setup can't replace a program that's running: it starts once this process is gone, into the same folder, silently
    // (packaging/installer.iss opens the program again when given /relaunch=1).
    private static void RunSetup(string setup, bool relaunch)
    {
        string args = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"{Folder}\"" + (relaunch ? " /relaunch=1" : "");
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        string script = $"Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue; Start-Process -FilePath {Quote(setup)} -ArgumentList {Quote(args)}";
        Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    // Linux: every file of the new folder takes the place of the old one, which is renamed, not overwritten (the running
    // program goes on from the renamed file); the .old ones go at the next start.
    private static bool SwapFolder(string from)
    {
        try
        {
            foreach (var src in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string dst = Path.Combine(Folder, Path.GetRelativePath(from, src));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (File.Exists(dst)) File.Move(dst, dst + ".old", true);
                File.Move(src, dst);
            }
            return true;
        }
        catch { return false; }
    }

    // macOS: the new .app takes the old one's place, and the old one is moved aside (the running copy goes on from there).
    private static bool SwapBundle(string from)
    {
        if (Bundle is not { } bundle) return false;
        string old = Path.Combine(Path.GetDirectoryName(bundle)!, ".ump-installer-old-" + Environment.ProcessId);
        try
        {
            Directory.Move(bundle, old);
            try { Directory.Move(from, bundle); }
            catch
            {
                Directory.Move(old, bundle);
                return false;
            }
            try { Directory.Delete(Path.GetDirectoryName(from)!, true); }
            catch { }
            return true;
        }
        catch { return false; }
    }
}
