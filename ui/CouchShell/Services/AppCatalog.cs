using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CouchShell.Services;

public sealed record AppDefinition(
    string Id,
    string Name,
    string Category,
    string Type,
    string Target,
    string? Args = null,
    string? ProcessName = null,
    string? Image = null,
    string? Controller = null,
    string? UserAgent = null,
    string? Backdrop = null,
    string? Logo = null,
    string? Color = null,
    string? Window = null,
    string? InstallFolder = null,
    string? Source = null,
    string? Watch = null,
    string? Icon = null,
    string? Requires = null)
{
    // As stored in the app:<id> hash in Tachyon. Empty optional fields are left out.
    public IEnumerable<string> ToHashFields()
    {
        string[] fields = ["id", Id, "name", Name, "category", Category, "type", Type, "target", Target];
        foreach (string field in fields) yield return field;
        foreach ((string name, string? value) in new[] { ("args", Args), ("processName", ProcessName), ("image", Image), ("controller", Controller), ("userAgent", UserAgent),
            ("backdrop", Backdrop), ("logo", Logo), ("color", Color), ("window", Window), ("installFolder", InstallFolder), ("source", Source), ("watch", Watch), ("icon", Icon) })
        {
            if (string.IsNullOrEmpty(value)) continue;
            yield return name;
            yield return value;
        }
    }

    public static AppDefinition? FromHash(Dictionary<string, string> fields)
    {
        if (!fields.TryGetValue("id", out string? id) || !fields.TryGetValue("name", out string? name)) return null;
        return new AppDefinition(
            id,
            name,
            fields.GetValueOrDefault("category", AppCatalog.DefaultCategory),
            fields.GetValueOrDefault("type", ""),
            fields.GetValueOrDefault("target", ""),
            fields.GetValueOrDefault("args"),
            fields.GetValueOrDefault("processName"),
            fields.GetValueOrDefault("image"),
            fields.GetValueOrDefault("controller"),
            fields.GetValueOrDefault("userAgent"),
            fields.GetValueOrDefault("backdrop"),
            fields.GetValueOrDefault("logo"),
            fields.GetValueOrDefault("color"),
            fields.GetValueOrDefault("window"),
            fields.GetValueOrDefault("installFolder"),
            fields.GetValueOrDefault("source"),
            fields.GetValueOrDefault("watch"),
            fields.GetValueOrDefault("icon"));
    }

    // How the controller drives this app, if it's a web app. Games read the controller themselves.
    // Anything else that can't use a controller, like a game launcher, asks for it with "controller".
    public WebControlStyle? WebControl => Type != "web" && Controller == null
        ? null
        : Controller == "keys" ? WebControlStyle.Keys : WebControlStyle.Pointer;
}

// Reads and validates apps.json. A bad entry is logged and skipped, never fatal.
public static partial class AppCatalog
{
    public const string DefaultCategory = "apps";
    private static readonly string[] Categories = ["games", "watch", DefaultCategory];

    // The same rule couchd enforces: safe in a space-separated request and in a key name.
    [GeneratedRegex("^[a-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    // Everything on the home screen: your own additions first, then the apps that ship with
    // CouchShell, then installed games, then the launchers. Your own file can replace a built-in app by reusing its id.
    public static List<AppDefinition> LoadAll(Action<string> log)
    {
        string personal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchShell", "apps.json");
        string bundled = Path.Combine(AppContext.BaseDirectory, "config", "apps.json");

        List<AppDefinition> own = File.Exists(personal) ? LoadFile(personal, log) : [];
        string steam = SteamLibrary.FindSteamFolder();
        return Merge(own, LoadFile(bundled, log), SteamLibrary.Scan(steam, log), GameLibraries.FindGames(log),
                GameLibraries.FindLaunchers(steam, log))
            .Where(IsInstalled)
            .ToList();
    }

    // A tile that can only fail isn't worth showing. Only programs can be checked this way.
    public static bool IsInstalled(AppDefinition app) =>
        (app.Type != "exe" || File.Exists(Environment.ExpandEnvironmentVariables(app.Target))) &&
        (app.InstallFolder == null || Directory.Exists(app.InstallFolder)) &&
        (app.Requires == null || File.Exists(Environment.ExpandEnvironmentVariables(app.Requires)));

    // Earlier lists win when two apps share an id.
    public static List<AppDefinition> Merge(params IEnumerable<AppDefinition>[] lists) =>
        lists.SelectMany(list => list).DistinctBy(app => app.Id).ToList();

    public static List<AppDefinition> LoadFile(string path, Action<string> log)
    {
        try
        {
            return Parse(File.ReadAllText(path), Path.GetDirectoryName(path) ?? "", log);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"Could not read {path}: {e.Message}");
            return [];
        }
    }

    public static List<AppDefinition> Parse(string json, string baseFolder, Action<string> log)
    {
        var apps = new List<AppDefinition>();
        var seen = new HashSet<string>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("apps", out JsonElement entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                log("apps.json has no \"apps\" list.");
                return apps;
            }

            int index = 0;
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                index++;
                AppDefinition? app = ParseEntry(entry, baseFolder, out string? problem);
                if (app == null)
                {
                    log($"Skipping app #{index} in apps.json: {problem}");
                }
                else if (!seen.Add(app.Id))
                {
                    log($"Skipping app #{index} in apps.json: duplicate id '{app.Id}'.");
                }
                else
                {
                    apps.Add(app);
                }
            }
        }
        catch (JsonException e)
        {
            log($"apps.json isn't valid JSON: {e.Message}");
        }
        return apps;
    }

    private static AppDefinition? ParseEntry(JsonElement entry, string baseFolder, out string? problem)
    {
        problem = null;
        if (entry.ValueKind != JsonValueKind.Object)
        {
            problem = "it isn't an object.";
            return null;
        }

        string? id = Text(entry, "id");
        string? name = Text(entry, "name");
        string? type = Text(entry, "type");
        string? target = Text(entry, "target");
        string? processName = Text(entry, "processName");
        string category = Text(entry, "category") ?? DefaultCategory;

        if (id == null || !IdPattern().IsMatch(id))
        {
            problem = "\"id\" must be lowercase letters, digits, - or _.";
        }
        else if (name == null)
        {
            problem = "\"name\" is missing.";
        }
        else if (target == null)
        {
            problem = "\"target\" is missing.";
        }
        else if (type is not ("exe" or "web" or "steam" or "shell"))
        {
            problem = "\"type\" must be exe, web, steam or shell.";
        }
        else if (type == "shell" && !target.StartsWith(@"shell:AppsFolder\", StringComparison.Ordinal))
        {
            problem = "a shell app's target must start with shell:AppsFolder\\.";
        }
        else if (!Categories.Contains(category))
        {
            problem = $"\"category\" must be one of {string.Join(", ", Categories)}.";
        }
        else if (type == "web" && !target.StartsWith("http://") && !target.StartsWith("https://"))
        {
            problem = "a web app needs an http or https address.";
        }
        else if (type == "steam" && !target.All(char.IsAsciiDigit))
        {
            problem = "a Steam app needs a numeric target.";
        }
        else if (Text(entry, "controller") is not (null or "pointer" or "keys"))
        {
            problem = "\"controller\" must be pointer or keys.";
        }
        if (problem != null) return null;

        return new AppDefinition(id!, name!, category, type!, target!, Text(entry, "args"), processName,
            ResolveImage(Text(entry, "image"), baseFolder), Text(entry, "controller"), Text(entry, "userAgent"),
            Logo: Text(entry, "logo"), Color: ValidColor(Text(entry, "color")), Window: Text(entry, "window"), Requires: Text(entry, "requires"));
    }

    // A missing image isn't an error: the tile just shows its name.
    private static string? ResolveImage(string? image, string baseFolder)
    {
        if (image == null) return null;
        string path = Path.GetFullPath(image, string.IsNullOrEmpty(baseFolder) ? Directory.GetCurrentDirectory() : baseFolder);
        return File.Exists(path) ? path : null;
    }

    // Goes straight into a WPF brush, where a bad value would be a binding error.
    private static string? ValidColor(string? color) =>
        color != null && ColorPattern().IsMatch(color) ? color : null;

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
