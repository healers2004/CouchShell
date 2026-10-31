using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using CouchShell.Platform;

namespace CouchShell.Services;

// Everything in Windows' own "All apps" list, including Store apps, for the Search screen.
public static class InstalledApps
{
    private const string AppsFolder = @"shell:AppsFolder\";
    private const int IconSize = 128;

    // Entries in the Start menu that aren't really apps.
    private static readonly string[] NotAppWords =
        ["uninstall", "readme", "read me", "documentation", "manual", "release notes", "website", "support center", "help"];
    private static readonly string[] NotAppEndings = [".html", ".htm", ".txt", ".pdf", ".url", ".chm"];

    // Reads the list and icons. Slow the first time, and needs an STA thread, so run it off the UI thread.
    public static List<AppDefinition> Load(Action<string> log)
    {
        var apps = new List<AppDefinition>();
        try
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new COMException("Shell.Application is missing.");
            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (dynamic item in shell.NameSpace("shell:AppsFolder").Items())
            {
                string name = item.Name;
                string path = item.Path;
                if (IsLaunchable(name, path)) apps.Add(ToApp(name, path, CachedIcon(path, log)));
            }
        }
        catch (COMException e)
        {
            log($"Could not read the list of installed apps: {e.Message}");
        }
        return Sorted(apps);
    }

    public static bool IsLaunchable(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if (NotAppEndings.Any(ending => path.EndsWith(ending, StringComparison.OrdinalIgnoreCase))) return false;
        return !NotAppWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    public static AppDefinition ToApp(string name, string path, string? icon) =>
        new("search-" + ShortHash(path), name, "apps", "shell", AppsFolder + path,
            Controller: "pointer", Source: "Installed app", Icon: icon);

    // Names starting with the text come first, then names containing it.
    public static List<AppDefinition> Search(IEnumerable<AppDefinition> apps, string query)
    {
        string text = query.Trim();
        return apps
            .Where(app => app.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(app => app.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<AppDefinition> Sorted(List<AppDefinition> apps) =>
        apps.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    // Icons are saved as files, so they load quickly next time and tiles can show them by path.
    private static string? CachedIcon(string path, Action<string> log)
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchShell", "icons");
        string file = Path.Combine(folder, ShortHash(path) + ".png");
        if (File.Exists(file)) return file;

        try
        {
            BitmapSource? icon = WindowsNative.GetShellIcon(AppsFolder + path, IconSize);
            if (icon == null) return null;
            Directory.CreateDirectory(folder);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(icon));
            using FileStream stream = File.Create(file);
            encoder.Save(stream);
            return file;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
            log($"Could not save an app icon: {e.Message}");
            return null;  // the tile shows the app's first letter instead
        }
    }
}
