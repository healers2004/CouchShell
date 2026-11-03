using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CouchShell.Services;

// Finds installed Steam games. Steam keeps a small text file (appmanifest_<id>.acf) per installed
// game in each library's steamapps folder, holding the real name and id; the folders under
// steamapps\common only have install folder names.
public static partial class SteamLibrary
{
    private const string DefaultFolder = @"C:\Program Files (x86)\Steam";
    private const int FullyInstalled = 4;  // a bit in the manifest's StateFlags

    // These appear in the same list but aren't games: Steam's support packages, and software that
    // Steam sells alongside games. The manifests don't say which is which.
    private static readonly string[] HiddenAppIds = ["228980", "431960"];  // redistributables, Wallpaper Engine
    private static readonly string[] HiddenNamePrefixes = ["Steamworks", "Steam Linux Runtime", "Proton"];
    private static readonly string[] ArtworkNames = ["header.jpg", "library_header.jpg", "library_hero.jpg"];
    private static readonly string[] BackdropNames = ["library_hero.jpg"];
    private static readonly string[] TitleImageNames = ["logo.png"];

    // A `"key"  "value"` line. Values escape backslashes as \\.
    [GeneratedRegex("\"([^\"]+)\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex KeyValue();

    public static string FindSteamFolder()
    {
        string? registered = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        return string.IsNullOrEmpty(registered) ? DefaultFolder : registered.Replace('/', Path.DirectorySeparatorChar);
    }

    public static List<AppDefinition> Scan(string steamFolder, Action<string> log)
    {
        var games = new Dictionary<string, AppDefinition>();
        foreach (string library in FindLibraries(steamFolder, log))
        {
            string steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps)) continue;
            foreach (string manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                try
                {
                    AppDefinition? game = ParseManifest(File.ReadAllText(manifest), steamFolder, library);
                    if (game != null) games[game.Id] = game;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log($"Could not read {manifest}: {e.Message}");
                }
            }
        }
        return games.Values.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static AppDefinition? ParseManifest(string text, string steamFolder, string? library = null)
    {
        Dictionary<string, string> fields = ReadFields(text);
        if (!fields.TryGetValue("appid", out string? appId) || !appId.All(char.IsAsciiDigit)) return null;
        if (!fields.TryGetValue("name", out string? name) || name.Length == 0) return null;
        if (HiddenAppIds.Contains(appId) || HiddenNamePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return null;

        // Mid-download or broken games can't be launched yet.
        if (!int.TryParse(fields.GetValueOrDefault("StateFlags"), out int flags) || (flags & FullyInstalled) == 0) return null;

        return new AppDefinition("steam-" + appId, name, "games", "steam", appId,
            Image: FindArtwork(steamFolder, appId, ArtworkNames),
            Backdrop: FindArtwork(steamFolder, appId, BackdropNames),
            TitleImage: FindArtwork(steamFolder, appId, TitleImageNames),
            InstallFolder: fields.TryGetValue("installdir", out string? folder)
                ? Path.Combine(library ?? steamFolder, "steamapps", "common", folder)
                : null);
    }

    // libraryfolders.vdf lists every library, including ones on other drives.
    public static IEnumerable<string> ParseLibraries(string libraryFoldersText) =>
        KeyValue().Matches(libraryFoldersText)
            .Where(match => match.Groups[1].Value == "path")
            .Select(match => Unescape(match.Groups[2].Value));

    private static List<string> FindLibraries(string steamFolder, Action<string> log)
    {
        var libraries = new List<string> { steamFolder };
        string listFile = Path.Combine(steamFolder, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(listFile)) libraries.AddRange(ParseLibraries(File.ReadAllText(listFile)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"Could not read {listFile}: {e.Message}");
        }
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, string> ReadFields(string text)
    {
        var fields = new Dictionary<string, string>();
        foreach (Match match in KeyValue().Matches(text))
        {
            // The first match wins: later sections reuse names like "name".
            fields.TryAdd(match.Groups[1].Value, Unescape(match.Groups[2].Value));
        }
        return fields;
    }

    private static string Unescape(string value) => value.Replace(@"\\", @"\").Replace("\\\"", "\"");

    // Steam caches each game's artwork. Versions differ: a prefix on the name, a folder per game,
    // or hashed folders inside that. The wide pictures come first, because they suit a tile best.
    private static string? FindArtwork(string steamFolder, string appId, string[] names)
    {
        string cache = Path.Combine(steamFolder, "appcache", "librarycache");
        string oldStyle = Path.Combine(cache, appId + "_header.jpg");
        if (names == ArtworkNames && File.Exists(oldStyle)) return oldStyle;

        string folder = Path.Combine(cache, appId);
        if (!Directory.Exists(folder)) return null;
        foreach (string name in names)
        {
            string? found = Directory.EnumerateFiles(folder, name, SearchOption.AllDirectories).FirstOrDefault();
            if (found != null) return found;
        }
        return null;
    }
}
