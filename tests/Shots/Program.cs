using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Installer;

string folder = args.FirstOrDefault(a => a != "perf") ?? Path.Combine(Path.GetTempPath(), "installer-shots");
bool perf = args.Contains("perf");
Directory.CreateDirectory(Path.Combine(folder, "shots"));
Environment.SetEnvironmentVariable("UMP_INSTALLER_DATA", Path.Combine(folder, "data"));

int wrong = 0;
void Check(bool ok, string what)
{
    if (ok) return;
    wrong++;
    Console.WriteLine("WRONG: " + what);
}

// ---- What the helper says

var said = HelperEvent.Parse("""{"event":"stage","stage":"Signing","progress":0.42}""");
Check(said is { Event: "stage" } && said.Text("stage") == "Signing" && said.Number("progress") == 0.42, "a stage and how far it is");
Check(HelperEvent.Parse("""{"event":"phone","what":"trust"}""")?.Phone == PhoneRequest.Trust, "what it asks of the phone");
Check(HelperEvent.Parse("""{"event":"done","moved":true}""")?.Flag("moved") == true, "a yes");
Check(HelperEvent.Parse("2026-11-17T13:16:41Z  INFO isideload: Loaded") == null && HelperEvent.Parse("[1,2]") == null, "a line that isn't an event is passed over");

// ---- How far it is

double last = -1;
foreach (var (stage, part) in new[] { ("Preparing", 0.0), ("Preparing", 1.0), ("Signing", 0.1), ("Signing", 0.5), ("Installing", 0.2), ("Installing", 0.9), ("Installing", 1.0) })
{
    var (friendly, overall) = Progress.Of(stage, part);
    Check(friendly != null, "a name for " + stage);
    Check(overall >= last, $"progress only goes forward: {stage} {part}");
    last = overall;
}
Check(Math.Abs(last - 1) < 1e-9, "it ends at 100%");
Check(Progress.Of("Something new", 0.5).Friendly == null, "an unknown stage says nothing rather than something wrong");
Check(Progress.Of("Pairing", 0).Friendly != null && Progress.Of("Pairing", 0.5).Overall == 1, "SideStore's pairing has a name, and once placed it's the end");

// ---- What the camera opens

const string source = "https://github.com/KirboHQ/UltimateMP3Player/releases/latest/download/sidestore-source.json";
Check(Releases.SideStoreSourceLink == "sidestore://source?url=" + Uri.EscapeDataString(source), "the code adds the app's own source to SideStore");

// ---- What went wrong

Check(Errors.For("Error: -80045 cannot").Title == L.T("Apple is refusing the login right now"), "Apple refusing");
Check(Errors.For("Auth error -20970: Your account information was entered incorrectly.").Title == L.T("Wrong email or password"), "an address Apple doesn't know is a wrong email");
Check(Errors.For("Error: 0xe8008024").Other == L.T("Use another Apple ID"), "an account Apple rejects leads to another account");
Check(Errors.For("Your session has expired. Please log in.").Other == L.T("Sign in again"), "an expired session leads to signing in");
Check(Errors.For("No such host is known. (github.com:443)").Title == L.T("No internet connection"), "no internet");
Check(Errors.For("Something 15030 happened").Title == L.T("Something didn't work"), "a number that merely contains 503 is not Apple refusing");
Check(Errors.For("Failed to provision with the anisette server").Title == L.T("I can't reach the signing service"), "the service down");
Check(Errors.For("nothing known here").Title == L.T("Something didn't work"), "anything else");
Check(Errors.For("Auth error -22406: Enter the correct password for this Apple Account.").Title == L.T("Wrong email or password"), "a wrong password, as the helper says it");
Check(Errors.For("error sending request for url (https://developerservices2.apple.com/x): client error (Connect): tcp connect error").Title == L.T("I can't reach the signing service"), "Apple out of reach");
Check(Errors.For("\n ● device not found\n").Title == L.T("I lost your iPhone"), "the phone gone");
Check(Errors.For(" ● Failed to parse URL Bag plist\n ● Serde(\"invalid type: string \\\"Threads\\\", expected a map\")").Title == L.T("I can't reach the signing service"), "Apple's first answer garbled");
Check(Errors.For("tcp connect error").AppleSide && Errors.For("Error: -80045").AppleSide && !Errors.For("-22406").AppleSide && !Errors.For("device not found").AppleSide, "Apple's status page offered only when it may be Apple's sign-in that's down");
Check(Errors.For("Not signed in").Other == L.T("Sign in again"), "no sign-in kept");

// ---- Versions

Check(Releases.IsNewer("5.1.1", "5.1.0") && !Releases.IsNewer("5.1.0", "5.1.1") && !Releases.IsNewer("5.1.0", "5.1.0"), "newer versions");
Check(Releases.IsNewer("5.10.0", "5.9.9"), "versions are numbers, not text");

// ---- Which SideStore goes on the phone

const string sideStoreIpa = """{"name":"SideStore.ipa","browser_download_url":"https://github.com/SideStore/SideStore/releases/download/0.7.1/SideStore.ipa","digest":"sha256:abc"}""";
string SideStoreRelease(string tag, bool prerelease, string asset) => $$"""{"tag_name":"{{tag}}","prerelease":{{(prerelease ? "true" : "false")}},"assets":[{"name":"build-logs.zip"},{{asset}}]}""";
Check(Releases.SideStoreFrom(SideStoreRelease("0.7.1", false, sideStoreIpa)) == ("0.7.1", "https://github.com/SideStore/SideStore/releases/download/0.7.1/SideStore.ipa", "abc"), "SideStore's newest release, with GitHub's hash");
Check(Releases.SideStoreFrom(SideStoreRelease("0.7.0-alpha", false, sideStoreIpa))?.Version == Releases.SideStoreVersion, "the version proven here is new enough");
Check(Releases.SideStoreFrom(SideStoreRelease("0.6.4", false, sideStoreIpa)) == null, "never older than the version proven here");
Check(Releases.SideStoreFrom(SideStoreRelease("0.7.1", true, sideStoreIpa)) == null, "never a prerelease");
Check(Releases.SideStoreFrom(SideStoreRelease("0.7.1", false, """{"name":"SideStore.ipa","browser_download_url":"https://github.com/x/SideStore.ipa"}""")) == null, "never without GitHub's hash");
Check(Releases.SideStoreFrom(SideStoreRelease("nightly", false, sideStoreIpa)) == null, "never a tag that isn't a version");

// ---- This program updating itself: never a build from the sources

Check(!SelfUpdate.CanUpdate, "a build from the sources doesn't replace itself");
Check(SelfUpdate.Asset.StartsWith("UltimateMP3Player-Installer-") && File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".github", "workflows", "release.yml")).Contains(SelfUpdate.Asset), "it looks for the package release.yml makes for this system");

// ---- The helper beside the program (built in helper/ with cargo build --release, copied by Installer.csproj)

Check(File.Exists(HelperRun.Exe), "the helper is beside the program: build it in helper/ with cargo build --release");
if (File.Exists(HelperRun.Exe))
    Check(await Helper.AccountAsync(CancellationToken.None) == null, "the helper answers, and in a new folder nobody is signed in");

// ---- A file that's half or wrong never gets used

string good = Path.Combine(folder, "hash-test");
File.Delete(good);
await using (var server = new Installer.Tests.OneFile("hello"))
{
    await Setup.DownloadAsync(server.Url, good, "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", _ => { }, CancellationToken.None);
    Check(File.ReadAllText(good) == "hello", "a download with the right hash");
    File.Delete(good);
    try
    {
        await Setup.DownloadAsync(server.Url, good, new string('0', 64), _ => { }, CancellationToken.None);
        Check(false, "a download with the wrong hash has to fail");
    }
    catch (IOException) { }
    Check(!File.Exists(good) && !File.Exists(good + ".part"), "nothing left of a wrong download");
}

// ---- The screens

var builder = AppBuilder.Configure<App>().With(new FontManagerOptions
{
    DefaultFamilyName = OperatingSystem.IsWindows() ? "Segoe UI Variable Text, Segoe UI" : "avares://UltimateMP3PlayerInstaller/Assets/Fonts#Selawik",
});
if (perf) builder.UsePlatformDetect().SetupWithoutStarting();
else builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

MainWindow.Previewing = true;
var names = new[] { "welcome", "why", "computer", "blocker", "connect", "connected", "choice", "account", "account-error", "code", "code2", "install", "install-trust", "install-confirm", "moving", "install-sidestore", "activate", "activate2", "activate3", "activate4", "trust", "trust3", "sidestore", "sidestore2", "sidestore3", "done", "renewed", "done-sidestore", "renewed-sidestore", "home", "home-away", "home-wsl", "home-sidestore", "problem" };

if (perf)
{
    if (Environment.GetEnvironmentVariable("PERF_ONLY") is { Length: > 0 } only) names = only.Split(',');
    await Installer.Tests.Perf.RunAsync(names, folder);
    Environment.Exit(wrong == 0 ? 0 : 1);
}

// SHOTS_SCALE=2: the pictures at twice the size, for the README (tests/Shots/readme.py), the window drawn again into a bigger one.
double scale = double.TryParse(Environment.GetEnvironmentVariable("SHOTS_SCALE"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 1;
Motion.Instant = true;
var window = new MainWindow();
window.Show();
foreach (var name in names)
{
    window.Preview(name);
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
    Avalonia.Media.Imaging.Bitmap? frame;
    if (scale == 1) frame = window.CaptureRenderedFrame();
    else
    {
        var big = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)(window.ClientSize.Width * scale), (int)(window.ClientSize.Height * scale)), new Vector(96 * scale, 96 * scale));
        big.Render(window);
        frame = big;
    }
    Check(frame != null, "a picture of " + name);
    frame?.Save(Path.Combine(folder, "shots", name + ".png"));
}

Console.WriteLine(wrong == 0 ? "shots: ok" : $"shots: {wrong} wrong");
return wrong == 0 ? 0 : 1;
