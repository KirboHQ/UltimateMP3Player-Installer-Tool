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

// ---- What xtool prints

List<Out> Feed(params string[] chunks)
{
    var parser = new XtoolParser();
    var all = chunks.SelectMany(parser.Feed).ToList();
    all.AddRange(parser.End());
    return all;
}

var stages = Feed("\n[Signing]   8%", "\r[Signing]  42%", "\r[Signing] 100%\n[Uploading] ...\n");
Check(stages.Count == 4, "stages seen one by one, also before their \\r: " + stages.Count);
Check(stages[1] is { Kind: OutKind.Stage, Text: "Signing", Percent: 0.42 }, "a stage and its percent");
Check(stages[3] is { Kind: OutKind.Stage, Text: "Uploading", Percent: null }, "a stage without a percent");

var login = Feed("Heads up:\n  This works by emulating a virtual Mac\n", "Apple ID: ");
Check(login.Count == 3 && login[2] is { Kind: OutKind.Prompt, Prompt: PromptKind.AppleId }, "a question without a newline fires at once");
Check(Feed("Password: ").Count == 1, "a question isn't told twice when its line ends");
Check(Feed("Code: ")[0].Prompt == PromptKind.Code, "the code");
Check(Feed("Continue? (yes/no): ")[0].Prompt == PromptKind.Confirm, "a yes or no");
Check(Feed("0: A (X)\n1: B (Y)\nChoice (0-1): ")[2].Prompt == PromptKind.Choice, "a choice");
var trust = Feed("\nPlease tap 'trust' on your device...", "\nContinuing...");
Check(trust.Count == 2 && trust[0].Text.StartsWith("Please tap") && trust[1].Text.StartsWith("Continuing"), "what it asks of the phone, without waiting for a newline");
Check(Feed("Some line\nError: x")[1].Text == "Error: x", "a last line without a newline");
Check(Feed("Installing to device: [iPhone] (udid: 1)\n")[0].Kind == OutKind.Line, "brackets inside a line aren't a stage");

// ---- How far it is

var order = new[] { "Unpacking app", "Preparing device", "Provisioning", "Signing", "Packaging", "Connecting", "Uploading", "Installing" };
double last = -1;
foreach (var s in order)
{
    var (friendly, start) = Progress.Of(s, 0);
    var (_, end) = Progress.Of(s, 1);
    Check(friendly != null, "a name for " + s);
    Check(start >= last && end > start, "progress only goes forward: " + s);
    last = end;
}
Check(Math.Abs(last - 1) < 1e-9, "it ends at 100%");
Check(Progress.Of("Something new", 0.5).Friendly == null, "an unknown stage says nothing rather than something wrong");

// ---- What went wrong

Check(Errors.For("Error: -80045 cannot").Title == L.T("Apple is refusing the login right now"), "Apple refusing");
Check(Errors.For("Error: 0xe8008024").Other == L.T("Use another Apple ID"), "an account Apple rejects leads to another account");
Check(Errors.For("Your session has expired. Please log in.").Other == L.T("Sign in again"), "an expired session leads to signing in");
Check(Errors.For("No such host is known. (github.com:443)").Title == L.T("No internet connection"), "no internet");
Check(Errors.For("Something 15030 happened").Title == L.T("Something didn't work"), "a number that merely contains 503 is not Apple refusing");
Check(Errors.For("HTTPClientError.deadlineExceeded").Title == L.T("I can't reach the signing service"), "the service down");
Check(Errors.For("nothing known here").Title == L.T("Something didn't work"), "anything else");

// ---- Versions and paths

Check(Releases.IsNewer("5.1.1", "5.1.0") && !Releases.IsNewer("5.1.0", "5.1.1") && !Releases.IsNewer("5.1.0", "5.1.0"), "newer versions");
Check(Releases.IsNewer("5.10.0", "5.9.9"), "versions are numbers, not text");
Check(Setup.ToLinuxPath(@"C:\Users\Some One\x.ipa") == "/mnt/c/Users/Some One/x.ipa", "a Windows path in Linux");
Check(!Setup.WslArgs("sh", "-c", "set -e\r\ncd /\r\n").Any(a => a.Contains('\r')), "a script reaches Linux with Linux's line endings, whatever the checkout has");
const string noVmService = "Wsl/Service/RegisterDistro/CreateVm/HCS/HCS_E_SERVICE_NOT_AVAILABLE";
Check(Setup.WslFailure(noVmService, platform: false) is BlockedException { Blocker: Blocker.WslConsent, Message: "ask" }, "the platform missing leads to asking to turn it on");
Check(Setup.WslFailure(noVmService, platform: true) is BlockedException { Blocker: Blocker.Virtualization }, "the platform there and still no service leads to the BIOS, not round again");
string tools = Path.Combine(folder, "tools");
Directory.CreateDirectory(tools);
File.Delete(Path.Combine(tools, "unzip"));
File.WriteAllText(Path.Combine(tools, "zip"), "");
Check(!Setup.HasZipTools(tools), "zip alone isn't enough");
File.WriteAllText(Path.Combine(tools, "unzip"), "");
Check(Setup.HasZipTools("nowhere" + Path.PathSeparator + tools), "zip and unzip found along the path");

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
var names = new[] { "welcome", "why", "computer", "blocker", "connect", "connected", "account", "account-error", "code", "code2", "install", "install-trust", "install-confirm", "activate", "activate2", "activate3", "activate4", "trust", "trust3", "done", "renewed", "home", "home-away", "problem" };

if (perf)
{
    if (Environment.GetEnvironmentVariable("PERF_ONLY") is { Length: > 0 } only) names = only.Split(',');
    await Installer.Tests.Perf.RunAsync(names, folder);
    Environment.Exit(wrong == 0 ? 0 : 1);
}

Motion.Instant = true;
var window = new MainWindow();
window.Show();
foreach (var name in names)
{
    window.Preview(name);
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
    var frame = window.CaptureRenderedFrame();
    Check(frame != null, "a picture of " + name);
    frame?.Save(Path.Combine(folder, "shots", name + ".png"));
}

Console.WriteLine(wrong == 0 ? "shots: ok" : $"shots: {wrong} wrong");
return wrong == 0 ? 0 : 1;
