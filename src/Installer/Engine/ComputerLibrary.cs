using System.IO.Compression;
using System.Text.Json;

namespace Installer;

// What the person chose to bring to the iPhone. The music files and the statistics are the songs', so they come only with them.
public sealed record Bringing(bool Songs, bool Files, bool Stats, bool Look, bool Settings)
{
    public bool Any => Songs || Look || Settings;
}

// What the app on this computer has, to bring to the one on the iPhone: its data folder, read as it is (the app can be open, and
// needn't know). The data goes as one of the app's own backups, which the one on the phone already reads, and the music beside it.
public sealed class ComputerLibrary
{
    public sealed record Item(string From, string To, long Size);

    public int Songs { get; init; }
    public int Playlists { get; init; }
    public int Profiles { get; init; }
    public string Version { get; init; } = "";
    // The songs saved as files on this computer (the others play from their link), and their videos.
    public IReadOnlyList<Item> Files { get; init; } = Array.Empty<Item>();
    public int SavedSongs => Files.Count(f => f.To.StartsWith("music/", StringComparison.Ordinal));
    public long FileBytes => Files.Sum(f => f.Size);

    public static string Size(long bytes) => bytes >= 1_000_000_000 ? (bytes / 1e9).ToString("0.0", L.Culture) + " GB" : Math.Max(1, bytes / 1_000_000) + " MB";

    // Where the app keeps its data (AppPaths.DataDir in its Core).
    private static string Folder => Environment.GetEnvironmentVariable("UMP_DATA") is { Length: > 0 } elsewhere
        ? elsewhere
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ultimate MP3 Player");

    // Null when the app isn't on this computer, or has no songs yet.
    public static ComputerLibrary? Find()
    {
        try
        {
            using var library = Read("library.json");
            var files = new List<Item>();
            int songs = 0;
            foreach (var track in library.RootElement.GetProperty("Tracks").EnumerateArray())
            {
                songs++;
                if (Text(track, "Id") is not { Length: > 0 } id || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) continue;
                string? audio = Text(track, "Path"), video = Text(track, "VideoPath");
                Add(audio, "music/" + id);
                if (video != audio) Add(video, "videos/" + id);
            }
            if (songs == 0) return null;

            using var list = Read("profiles.json");
            int profiles = 0, playlists = 0;
            foreach (var profile in list.RootElement.GetProperty("Profiles").EnumerateArray())
            {
                profiles++;
                try
                {
                    using var data = Read(Path.Combine("profiles", Text(profile, "Id") ?? "", "data.json"));
                    playlists += data.RootElement.GetProperty("Playlists").GetArrayLength();
                }
                // (a profile never opened has nothing yet)
                catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { }
            }

            string version = "";
            try
            {
                using var settings = Read("settings.json");
                version = Text(settings.RootElement, "LastVersion") ?? "";
            }
            catch (Exception e) when (e is IOException or JsonException) { }
            return new ComputerLibrary { Songs = songs, Playlists = playlists, Profiles = profiles, Version = version, Files = files };

            void Add(string? path, string to)
            {
                if (string.IsNullOrEmpty(path) || new FileInfo(path) is not { Exists: true } file) return;
                files.Add(new Item(path, to + file.Extension.ToLowerInvariant(), file.Length));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or NotSupportedException) { return null; }
    }

    // Where what's sent is got ready: emptied once it's there (Sent), since it's the person's own things.
    private static string Outbox => Path.Combine(Setup.DataDir, "outbox");

    // What's to send and where it goes inside the app on the phone, in order, as the helper's list: the path of that. ready.json is the
    // last: until it's there the app touches nothing.
    public string Pack(Bringing what)
    {
        Directory.CreateDirectory(Outbox);
        string data = Path.Combine(Outbox, "data.zip"), ready = Path.Combine(Outbox, "ready.json"), list = Path.Combine(Outbox, "list.json");
        using (var zip = new ZipArchive(File.Create(data), ZipArchiveMode.Create)) WriteData(zip, what);

        var items = what is { Songs: true, Files: true } ? Files.ToList() : new List<Item>();
        items.Add(new Item(data, "data.zip", new FileInfo(data).Length));
        WriteJson(ready, w =>
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteString("id", Guid.NewGuid().ToString("N"));
            w.WriteString("made", DateTime.UtcNow);
            w.WriteString("computer", Environment.MachineName);
            w.WriteString("app", Version);
            w.WriteNumber("songs", Songs);
            w.WriteNumber("playlists", Playlists);
            w.WriteNumber("files", items.Count - 1);
            w.WriteNumber("bytes", items.Sum(i => i.Size));
            w.WriteStartArray("take");
            if (what.Songs) w.WriteStringValue("songs");
            if (what is { Songs: true, Stats: true }) w.WriteStringValue("stats");
            if (what.Look) w.WriteStringValue("look");
            if (what.Settings) w.WriteStringValue("settings");
            w.WriteEndArray();
            w.WriteEndObject();
        });
        items.Add(new Item(ready, "ready.json", 0));
        WriteJson(list, w =>
        {
            w.WriteStartArray();
            foreach (var item in items)
            {
                w.WriteStartObject();
                w.WriteString("from", item.From);
                w.WriteString("to", item.To);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        return list;
    }

    public static void Sent()
    {
        try { Directory.Delete(Outbox, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // As the app writes its backups (Backup.Write in its Core), without the keys and the sign-ins.
    private void WriteData(ZipArchive zip, Bringing what)
    {
        foreach (var name in new[] { "library.json", "settings.json", "profiles.json", "artists.json" }) Add(name);
        foreach (var folder in what.Songs ? new[] { "profiles", "covers", "lyrics" } : new[] { "profiles" })
        {
            string root = Path.Combine(Folder, folder);
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                if (Path.GetExtension(file).ToLowerInvariant() is not (".tmp" or ".bak" or ".unreadable")) Add(Path.GetRelativePath(Folder, file));
        }
        using (var entry = zip.CreateEntry("backup.json").Open())
        using (var info = new Utf8JsonWriter(entry))
        {
            info.WriteStartObject();
            info.WriteString("Created", DateTime.Now);
            info.WriteString("Device", Environment.MachineName);
            info.WriteString("Version", Version);
            info.WriteNumber("Songs", Songs);
            info.WriteNumber("Profiles", Profiles);
            info.WriteEndObject();
        }

        void Add(string name)
        {
            bool settings = Path.GetFileName(name) is "settings.json" or "data.json";
            bool picture = Path.GetExtension(name).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp";
            try
            {
                using var from = Open(name);
                // (read before its entry is made: one that can't be read, or cleaned, isn't in the zip at all)
                using Stream? clean = settings ? WithoutSecrets(from) : null;
                using var to = zip.CreateEntry(name.Replace('\\', '/'), picture ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open();
                (clean ?? from).CopyTo(to);
            }
            // (one that isn't there, or is being written right now, isn't a reason to fail the rest)
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }

    // The sign-ins and the keys of the person's accounts stay on this computer.
    internal static MemoryStream WithoutSecrets(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var clean = new MemoryStream();
        using (var writer = new Utf8JsonWriter(clean))
        {
            writer.WriteStartObject();
            foreach (var property in doc.RootElement.EnumerateObject())
                if (!new[] { "Token", "Secret", "Session", "Password", "Key" }.Any(end => property.Name.EndsWith(end, StringComparison.OrdinalIgnoreCase))) property.WriteTo(writer);
            writer.WriteEndObject();
        }
        clean.Position = 0;
        return clean;
    }

    // (the app may have it open, or be putting a new one in its place)
    private static FileStream Open(string name) => new(Path.Combine(Folder, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static JsonDocument Read(string name)
    {
        using var file = Open(name);
        return JsonDocument.Parse(file);
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void WriteJson(string path, Action<Utf8JsonWriter> write)
    {
        using var file = File.Create(path);
        using var writer = new Utf8JsonWriter(file);
        write(writer);
    }
}
