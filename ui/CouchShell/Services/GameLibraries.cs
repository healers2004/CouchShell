using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CouchShell.Services;

// One entry from Windows' list of installed programs, which most launchers register games in.
public sealed record UninstallEntry(string KeyName, string? DisplayName, string? InstallLocation, string? UninstallString);

// Finds games and launchers from outside Steam: Riot and Battle.net through Windows' installed
// programs list, and Epic through its manifest files.
public static partial class GameLibraries
{
    // Battle.net launches a game by a code that differs from its install id.
    private static readonly Dictionary<string, string> BattleNetLaunchCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["prometheus"] = "Pro",  // Overwatch
        ["wow"] = "WoW",
        ["d3"] = "D3",
        ["fenris"] = "Fen",      // Diablo IV
        ["osi"] = "OSI",         // Diablo II: Resurrected
        ["hero"] = "Hero",       // Heroes of the Storm
        ["hsb"] = "WTCG",        // Hearthstone
        ["s1"] = "S1",
        ["s2"] = "S2",
        ["w3"] = "W3",
    };

    [GeneratedRegex(@"^Riot Game ([a-z0-9_]+)\.([a-z0-9_]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex RiotKey();

    [GeneratedRegex(@"--uid=""?([^\s""]+)")]
    private static partial Regex BattleNetUid();

    [GeneratedRegex("^\"([^\"]+)\"")]
    private static partial Regex QuotedProgram();

    [GeneratedRegex("[^a-z0-9_-]")]
    private static partial Regex UnsafeIdCharacters();

    public static List<AppDefinition> FindGames(Action<string> log)
    {
        List<UninstallEntry> entries = ReadUninstallEntries(log);
        return [.. ParseRiot(entries), .. ParseBattleNet(entries), .. ScanEpic(log)];
    }

    // The launchers themselves, for browsing and installing from the couch.
    public static List<AppDefinition> FindLaunchers(string steamFolder, Action<string> log)
    {
        List<UninstallEntry> entries = ReadUninstallEntries(log);
        List<AppDefinition> launchers = ParseLaunchers(entries);
        launchers.Insert(0, Launcher("steam", "Steam", Path.Combine(steamFolder, "steam.exe"), steamFolder));
        return launchers;
    }

    public static List<AppDefinition> ParseRiot(IEnumerable<UninstallEntry> entries)
    {
        List<UninstallEntry> all = entries.ToList();
        string? client = RiotClient(all);
        if (client == null) return [];

        var games = new List<AppDefinition>();
        foreach (UninstallEntry entry in all)
        {
            Match key = RiotKey().Match(entry.KeyName);
            if (!key.Success) continue;
            string product = key.Groups[1].Value;
            // Only the main release: test servers would show up as duplicates.
            if (key.Groups[2].Value != "live" || product.Equals("Riot_Client", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(entry.InstallLocation)) continue;

            games.Add(new AppDefinition("riot-" + SafeId(product), Name(entry, product), "games", "exe", client,
                Args: $"--launch-product={product} --launch-patchline=live",
                Logo: "riot",
                InstallFolder: entry.InstallLocation.Replace('/', '\\'),
                Source: "Riot Games"));
        }
        return games;
    }

    public static List<AppDefinition> ParseBattleNet(IEnumerable<UninstallEntry> entries)
    {
        List<UninstallEntry> all = entries.ToList();
        UninstallEntry? launcher = all.Find(entry => entry.KeyName.Equals("Battle.net", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(launcher?.InstallLocation)) return [];
        string program = Path.Combine(launcher.InstallLocation, "Battle.net.exe");

        var games = new List<AppDefinition>();
        foreach (UninstallEntry entry in all)
        {
            if (entry.UninstallString?.Contains("Blizzard Uninstaller", StringComparison.OrdinalIgnoreCase) != true) continue;
            Match uid = BattleNetUid().Match(entry.UninstallString);
            if (!uid.Success || uid.Groups[1].Value.Equals("battle.net", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(entry.InstallLocation)) continue;

            string id = uid.Groups[1].Value;
            // A game without a known code still gets its page in Battle.net, one press from Play.
            string args = BattleNetLaunchCodes.TryGetValue(id, out string? code)
                ? $"--exec=\"launch {code}\""
                : $"--game={id}";
            games.Add(new AppDefinition("battlenet-" + SafeId(id), Name(entry, id), "games", "exe", program,
                Args: args, Logo: "battlenet", InstallFolder: entry.InstallLocation, Source: "Battle.net"));
        }
        return games;
    }

    public static AppDefinition? ParseEpicManifest(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("bIsIncompleteInstall", out JsonElement incomplete) && incomplete.ValueKind == JsonValueKind.True) return null;

            // Epic also installs things like Unreal Engine; only games are worth a tile.
            if (root.TryGetProperty("AppCategories", out JsonElement categories) && categories.ValueKind == JsonValueKind.Array &&
                !categories.EnumerateArray().Any(category => category.GetString() == "games"))
            {
                return null;
            }

            string? name = Text(root, "DisplayName");
            string? app = Text(root, "AppName");
            string? space = Text(root, "CatalogNamespace");
            string? item = Text(root, "CatalogItemId");
            string? folder = Text(root, "InstallLocation");
            if (name == null || app == null || space == null || item == null || folder == null) return null;

            string target = "com.epicgames.launcher://apps/" +
                $"{Uri.EscapeDataString(space)}%3A{Uri.EscapeDataString(item)}%3A{Uri.EscapeDataString(app)}?action=launch&silent=true";
            return new AppDefinition("epic-" + SafeId(app), name, "games", "uri", target,
                Logo: "epic", InstallFolder: folder.Replace('/', '\\'), Source: "Epic Games");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static List<AppDefinition> ParseLaunchers(IEnumerable<UninstallEntry> entries)
    {
        List<UninstallEntry> all = entries.ToList();
        var launchers = new List<AppDefinition>();

        UninstallEntry? battleNet = all.Find(entry => entry.KeyName.Equals("Battle.net", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(battleNet?.InstallLocation))
        {
            launchers.Add(Launcher("battlenet", "Battle.net", Path.Combine(battleNet.InstallLocation, "Battle.net.exe"), battleNet.InstallLocation));
        }

        string? riot = RiotClient(all);
        if (riot != null) launchers.Add(Launcher("riot", "Riot Client", riot, Path.GetDirectoryName(riot)!));

        UninstallEntry? epic = all.Find(entry => entry.DisplayName == "Epic Games Launcher");
        if (!string.IsNullOrWhiteSpace(epic?.InstallLocation))
        {
            string folder = Path.Combine(epic.InstallLocation, "Launcher");
            launchers.Add(Launcher("epic", "Epic Games", Path.Combine(folder, "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"), folder));
        }
        return launchers;
    }

    // These stay in the system tray when closed, so "running" means having a window open. They have
    // no controller support of their own, so the pointer drives them.
    private static AppDefinition Launcher(string logo, string name, string program, string folder) =>
        new("launcher-" + logo, name, "apps", "exe", program, Logo: logo, Controller: "pointer",
            InstallFolder: folder, Source: "Game launcher", Watch: "window");

    private static string? RiotClient(List<UninstallEntry> entries)
    {
        UninstallEntry? client = entries.Find(entry => entry.KeyName.StartsWith("Riot Game Riot_Client", StringComparison.OrdinalIgnoreCase));
        Match program = QuotedProgram().Match(client?.UninstallString ?? "");
        return program.Success ? program.Groups[1].Value : null;
    }

    private static List<AppDefinition> ScanEpic(Action<string> log)
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        var games = new List<AppDefinition>();
        if (!Directory.Exists(folder)) return games;
        foreach (string file in Directory.EnumerateFiles(folder, "*.item"))
        {
            try
            {
                AppDefinition? game = ParseEpicManifest(File.ReadAllText(file));
                if (game != null) games.Add(game);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"Could not read {file}: {e.Message}");
            }
        }
        return games;
    }

    private static List<UninstallEntry> ReadUninstallEntries(Action<string> log)
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        (RegistryHive Hive, RegistryView View)[] places =
        [
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default),
        ];

        var entries = new List<UninstallEntry>();
        foreach ((RegistryHive hive, RegistryView view) in places)
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? list = root.OpenSubKey(path);
                if (list == null) continue;
                foreach (string name in list.GetSubKeyNames())
                {
                    using RegistryKey? key = list.OpenSubKey(name);
                    if (key == null) continue;
                    entries.Add(new UninstallEntry(name, key.GetValue("DisplayName") as string,
                        key.GetValue("InstallLocation") as string, key.GetValue("UninstallString") as string));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                log($"Could not read the installed programs list: {e.Message}");
            }
        }
        return entries;
    }

    private static string Name(UninstallEntry entry, string fallback) =>
        string.IsNullOrWhiteSpace(entry.DisplayName) ? fallback : entry.DisplayName.Trim();

    private static string SafeId(string text) => UnsafeIdCharacters().Replace(text.ToLowerInvariant(), "-");

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 0
            ? value.GetString()
            : null;
}
