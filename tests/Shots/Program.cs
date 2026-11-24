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
// (never the app's real data on this computer)
string library = Path.Combine(folder, "library");
Environment.SetEnvironmentVariable("UMP_DATA", library);

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

// ---- What a phone says is on it, and who renews it

var app = PhoneApp.From(HelperEvent.Parse("""{"event":"app","trusted":true,"bundle":"com.kirbohq.ultimatemp3player.X","version":"5.2.0","renews":1,"expires":1795000000,"vpn":true,"sidestore":true}""")!);
Check(app is { Trusted: true, Version: "5.2.0", Renews: 1, Vpn: true, SideStore: true } && app.ExpiresUtc == DateTime.UnixEpoch.AddSeconds(1795000000), "what a phone says is on it");
Check(PhoneApp.From(HelperEvent.Parse("""{"event":"app","trusted":true,"bundle":null,"version":null,"renews":null,"expires":null,"vpn":false,"sidestore":false}""")!) is { Version: null, Renews: 0, ExpiresUtc: null }, "a phone without the app");
var known = new Memory.PhoneRecord { Version = "5.1.0", ByHelper = true, SideStore = true, InstalledUtc = DateTime.UtcNow };
Check(known.Learn(app) && known is { Version: "5.2.0", CanRenew: true, Vpn: true, How: Memory.Renewal.SideStore } && known.Until == app.ExpiresUtc, "what it says replaces what was known");
Check(known.Learn(app with { SideStore = false }) && known.How == Memory.Renewal.Cable, "SideStore deleted from the phone renews nothing");
known.Renews = true;
Check(known.How == Memory.Renewal.Itself && known.Learn(app with { Vpn = false }) && known.How == Memory.Renewal.MissingVpn, "the app renews itself, but not without LocalDevVPN");
Check(known.Learn(new PhoneApp(false)) && !known.Learn(new PhoneApp(true)), "no answer changes nothing; the app gone is gone");
Check(new Memory.PhoneRecord { ByHelper = false }.Learn(new PhoneApp(true)), "one installed with xtool has another name: not gone");
Check(Progress.Of("Renewing", 0.5).Friendly != null && Progress.Of("Renewing", 1).Overall == 1, "a renewal has a name, and an end");
Check(Releases.SideStoreTurnedAway(Releases.SideStoreVersion) && !Releases.SideStoreTurnedAway("0.7.1"), "a newer SideStore may sign in again");

// ---- Which SideStore goes on the phone

const string sideStoreIpa = """{"name":"SideStore.ipa","browser_download_url":"https://github.com/SideStore/SideStore/releases/download/0.7.1/SideStore.ipa","digest":"sha256:abc"}""";
string SideStoreRelease(string tag, bool prerelease, string asset) => $$"""{"tag_name":"{{tag}}","prerelease":{{(prerelease ? "true" : "false")}},"assets":[{"name":"build-logs.zip"},{{asset}}]}""";
Check(Releases.SideStoreFrom(SideStoreRelease("0.7.1", false, sideStoreIpa)) == ("0.7.1", "https://github.com/SideStore/SideStore/releases/download/0.7.1/SideStore.ipa", "abc"), "SideStore's newest release, with GitHub's hash");
Check(Releases.SideStoreFrom(SideStoreRelease("0.7.0-alpha", false, sideStoreIpa))?.Version == Releases.SideStoreVersion, "the version proven here is new enough");
Check(Releases.SideStoreFrom(SideStoreRelease("0.6.4", false, sideStoreIpa)) == null, "never older than the version proven here");
Check(Releases.SideStoreFrom(SideStoreRelease("0.7.1", true, sideStoreIpa)) == null, "never a prerelease");
Check(Releases.SideStoreFrom(SideStoreRelease("0.7.1", false, """{"name":"SideStore.ipa","browser_download_url":"https://github.com/x/SideStore.ipa"}""")) == null, "never without GitHub's hash");
Check(Releases.SideStoreFrom(SideStoreRelease("nightly", false, sideStoreIpa)) == null, "never a tag that isn't a version");

// ---- What the app on this computer has, to bring to the phone

if (Directory.Exists(library)) Directory.Delete(library, true);
Check(ComputerLibrary.Find() == null, "no app on this computer, nothing to bring");
void Put(string name, string text)
{
    string path = Path.Combine(library, name);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
}
string song = Path.Combine(library, "Music", "One.MP3"), songText = song.Replace("\\", "\\\\");
Put("Music/One.MP3", "music");
Put("library.json", $$"""{"Tracks":[{"Id":"aaa111","Path":"{{songText}}","VideoPath":"{{songText}}"},{"Id":"bbb222","Path":"","SourceUrl":"https://example.com"},{"Id":"../evil","Path":"{{songText}}"},{"Id":"ccc333","Path":"{{songText}}.gone"}]}""");
Check(ComputerLibrary.Find() == null, "without its profiles it isn't the app's data");
Put("profiles.json", """{"Profiles":[{"Id":"p1","Name":"One"},{"Id":"p2","Name":"Never opened"}]}""");
Put("profiles/p1/data.json", """{"Playlists":[{"Name":"A"},{"Name":"B"}],"Theme":"Dark","LastFmSession":"hush","ListenBrainzToken":"hush"}""");
Put("profiles/p1/data.json.bak", "hush");
Put("profiles/p1/stats.dat", "stats");
Put("settings.json", """{"LastVersion":"5.5.3","Language":"it","DiscordBotToken":"hush","LastFmKey":"hush","LastFmSecret":"hush"}""");
Put("covers/aaa111.jpg", "cover");
var mine = ComputerLibrary.Find();
Check(mine is { Songs: 4, Playlists: 2, Profiles: 2, SavedSongs: 1, FileBytes: 5, Version: "5.5.3" } && mine.Files[0].To == "music/aaa111.mp3", "the app's data: its songs, the ones saved as files, its playlists");
(string To, string From)[] Packed(Bringing what)
{
    using var list = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mine!.Pack(what)));
    return list.RootElement.EnumerateArray().Select(i => (i.GetProperty("to").GetString()!, i.GetProperty("from").GetString()!)).ToArray();
}
var all = Packed(new Bringing(true, true, true, true, true));
Check(all.Select(i => i.To).SequenceEqual(new[] { "music/aaa111.mp3", "data.zip", "ready.json" }), "the music, the data, and last what says it's all there");
using (var zip = System.IO.Compression.ZipFile.OpenRead(all[1].From))
{
    string Entry(string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
    Check(zip.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "backup.json", "covers/aaa111.jpg", "library.json", "profiles.json", "profiles/p1/data.json", "profiles/p1/stats.dat", "settings.json" }), "the data as one of the app's backups");
    Check(Entry("settings.json") == """{"LastVersion":"5.5.3","Language":"it"}""" && !zip.Entries.Any(e => Entry(e.FullName).Contains("hush")), "no key and no sign-in leaves this computer");
    Check(Entry("profiles/p1/data.json").Contains("\"Theme\":\"Dark\""), "the look is there");
}
string Take(Bringing what)
{
    using var ready = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Packed(what)[^1].From));
    return string.Join(',', ready.RootElement.GetProperty("take").EnumerateArray().Select(t => t.GetString()));
}
Check(Take(new Bringing(true, true, true, true, true)) == "songs,stats,look,settings" && Take(new Bringing(false, true, true, true, false)) == "look", "what was chosen; statistics only with the songs");
Check(Packed(new Bringing(false, true, true, true, false)).Length == 2, "nor the music without them");
ComputerLibrary.Sent();
Check(!Directory.Exists(Path.Combine(Setup.DataDir, "outbox")), "nothing of it is left behind");
Check(Progress.Of("Sending", 0.5) is { Friendly: not null, Overall: 0.5 }, "sending it has a name");
Check(Errors.For("afc error: No space left").Title == L.T("There's no room on your iPhone") && Errors.For(" ● The app on the phone can't take it: install its latest version first").Title == L.T("Install or update the app first"), "what can stop it");

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
var names = new[] { "welcome", "why", "needs", "computer", "blocker", "blocker-linux", "connect", "connected", "account", "account-error", "code", "code2", "install", "install-trust", "install-confirm", "moving", "install-sidestore", "activate", "activate2", "activate3", "activate4", "trust", "trust3", "self", "self2", "self3", "self4", "bring", "bring-look", "bringing", "brought", "home-bring", "home-bring-notice", "sidestore-stuck", "sidestore", "sidestore2", "sidestore3", "done", "done-itself", "renewed", "done-sidestore", "renewed-sidestore", "home", "home-away", "home-wsl", "home-itself", "home-left", "home-left-notice", "home-vpn", "home-vpn-notice", "home-self", "home-self-notice", "needs-itself", "home-tool", "home-tool-notice", "home-sidestore", "problem" };

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

// ---- The notices: Esc puts off what can wait, never what can't, and what takes something away isn't what Enter presses

var notices = window.FindControl<Panel>("NoticeLayer")!;
bool Open(string name)
{
    window.Preview(name);
    Dispatcher.UIThread.RunJobs();
    return notices.IsVisible;
}
void Escape()
{
    window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
    Dispatcher.UIThread.RunJobs();
}
Check(Open("home-left-notice") && window.FindControl<Button>("NoticeYes")!.IsFocused, "a notice comes up with its Yes ready");
Escape();
Check(!notices.IsVisible, "Esc puts it off");
Check(Open("install-trust"), "what to do on the phone comes up");
Escape();
Check(notices.IsVisible, "Esc doesn't close what's waiting for the phone");
Check(Open("install-confirm") && window.FindControl<Button>("NoticeNo")!.IsFocused, "with something taken away, No is the one ready");
// What a notice answers: its buttons, Esc, and another one in its place.
var question = new Notice(NoticeKind.Ask, "A question", "", "Yes", "No", Later: true);
(bool Done, bool? Answer) Said(Action act)
{
    var asked = window.AskAsync(question);
    Dispatcher.UIThread.RunJobs();
    act();
    Dispatcher.UIThread.RunJobs();
    return (asked.IsCompletedSuccessfully, asked.IsCompletedSuccessfully ? asked.Result : null);
}
void Press(string button) => window.FindControl<Button>(button)!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
Check(Said(() => Press("NoticeYes")) == (true, true), "Yes answers yes");
Check(Said(() => Press("NoticeNo")) == (true, false), "No answers no");
Check(Said(Escape) == (true, null), "Esc puts it off");
Task<bool?>? second = null;
async Task AskAgain(Task<bool?> first)
{
    await first;
    second = window.AskAsync(question);
}
var firstOne = window.AskAsync(question);
var chained = AskAgain(firstOne);
Press("NoticeYes");
Dispatcher.UIThread.RunJobs();
Check(firstOne.IsCompleted && second is { IsCompleted: false } && notices.IsVisible, "the one that comes after an answer stays");
Press("NoticeNo");
Dispatcher.UIThread.RunJobs();
Check(second is { IsCompletedSuccessfully: true, Result: false }, "and has its own answer");
// The link to a new version fits in its line, and the notice's own buttons do what they say.
Open("home-tool");
var link = window.FindControl<Button>("ToolUpdate")!;
Check(link.IsVisible && link.Bounds.Width < link.MaxWidth, "the link to the new version isn't cut");
bool closed = false;
window.Closed += (_, _) => closed = true;
Open("home-tool-notice");
Press("NoticeNo");
Dispatcher.UIThread.RunJobs();
Check(!closed && !notices.IsVisible, "\"When I close it\" waits");
Open("home-tool-notice");
Press("NoticeYes");
Dispatcher.UIThread.RunJobs();
Check(closed, "\"Restart now\" restarts");

Console.WriteLine(wrong == 0 ? "shots: ok" : $"shots: {wrong} wrong");
return wrong == 0 ? 0 : 1;
