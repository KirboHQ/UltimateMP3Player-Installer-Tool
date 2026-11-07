using Avalonia;
using Avalonia.Media;

namespace Installer;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // One connection of xtool to the phone (Relay): no window, nothing of Avalonia loaded, as quick as it can start.
        if (args is ["--usbmux"]) return Relay.Run();
        if (args is ["--uninstall"])
        {
            // Asked by Windows' uninstaller (packaging/installer.iss): whatever goes wrong here must not stop it.
            try { Setup.RemoveEverythingAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch { }
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new FontManagerOptions
        {
            // Segoe UI on Windows; elsewhere Selawik, Microsoft's open font made with Segoe UI's metrics.
            DefaultFamilyName = OperatingSystem.IsWindows() ? "Segoe UI Variable Text, Segoe UI" : "avares://UltimateMP3PlayerInstaller/Assets/Fonts#Selawik",
        })
        .With(new X11PlatformOptions { WmClass = "ultimate-mp3-player-installer", UseRetainedFramebuffer = true })
        .With(new MacOSPlatformOptions { ShowInDock = true })
        .LogToTrace();
}
