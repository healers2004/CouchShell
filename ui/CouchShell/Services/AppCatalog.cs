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
    string? Image = null)
{
    // As stored in the app:<id> hash in Tachyon. Empty optional fields are left out.
    public IEnumerable<string> ToHashFields()
    {
        string[] fields = ["id", Id, "name", Name, "category", Category, "type", Type, "target", Target];
        foreach (string field in fields) yield return field;
        foreach ((string name, string? value) in new[] { ("args", Args), ("processName", ProcessName), ("image", Image) })
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
            fields.GetValueOrDefault("image"));
    }
}

// Reads and validates apps.json. A bad entry is logged and skipped, never fatal.
public static partial class AppCatalog
{
    public const string DefaultCategory = "apps";
    private static readonly string[] Categories = ["games", "watch", DefaultCategory];

    // The same rule couchd enforces: safe in a space-separated request and in a key name.
    [GeneratedRegex("^[a-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    // A copy in %LOCALAPPDATA% wins, so it can be edited without touching the installed files.
    public static string DefaultPath()
    {
        string personal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchShell", "apps.json");
        return File.Exists(personal) ? personal : Path.Combine(AppContext.BaseDirectory, "config", "apps.json");
    }

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
        else if (type is not ("exe" or "web" or "steam"))
        {
            problem = "\"type\" must be exe, web or steam.";
        }
        else if (!Categories.Contains(category))
        {
            problem = $"\"category\" must be one of {string.Join(", ", Categories)}.";
        }
        else if (type == "web" && !target.StartsWith("http://") && !target.StartsWith("https://"))
        {
            problem = "a web app needs an http or https address.";
        }
        else if (type == "steam" && (processName == null || !target.All(char.IsAsciiDigit)))
        {
            problem = "a Steam app needs a numeric target and a processName.";
        }
        if (problem != null) return null;

        return new AppDefinition(id!, name!, category, type!, target!, Text(entry, "args"), processName,
            ResolveImage(Text(entry, "image"), baseFolder));
    }

    // A missing image isn't an error: the tile just shows its name.
    private static string? ResolveImage(string? image, string baseFolder)
    {
        if (image == null) return null;
        string path = Path.GetFullPath(image, string.IsNullOrEmpty(baseFolder) ? Directory.GetCurrentDirectory() : baseFolder);
        return File.Exists(path) ? path : null;
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
