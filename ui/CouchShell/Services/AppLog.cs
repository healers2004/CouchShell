using System.IO;

namespace CouchShell.Services;

// A small local log at %LOCALAPPDATA%\CouchShell\logs\ui.log. Logging must never take the UI down,
// so failures to write are ignored.
public static class AppLog
{
    private static readonly object Gate = new();

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchShell", "logs");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, "ui.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
