using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace CouchShell.Services;

// In an installed copy, Tachyon and couchd sit in a "backend" folder next to the program, and the
// UI runs them. A build from source has no such folder: tools/start-couchshell.ps1 runs them there.
// They outlive a UI crash on purpose, so a restarted UI finds the running app still tracked.
public static class BackendHost
{
    private const string DataArgument = "--dir data";

    private static string BackendFolder => Path.Combine(AppContext.BaseDirectory, "backend");

    public static string TachyonPath => Path.Combine(BackendFolder, "tachyon.exe");

    public static string CouchdPath => Path.Combine(BackendFolder, "couchd.exe");

    // Starts whichever isn't running. Returns false if one couldn't be started.
    public static bool Start(Action<string> log)
    {
        if (!File.Exists(TachyonPath) || !File.Exists(CouchdPath)) return true;  // built from source

        string home = Path.GetDirectoryName(AppLog.Folder)!;
        bool ok = true;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "data"));
            Directory.CreateDirectory(AppLog.Folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"Could not create {home}: {e.Message}");
            return false;
        }
        ok &= StartOne(TachyonPath, DataArgument, home, log);
        // couchd retries until Tachyon is up, so there's no need to wait for it here.
        ok &= StartOne(CouchdPath, "", home, log);
        return ok;
    }

    // Leaves nothing running after a normal exit. Tachyon writes before it acknowledges, so stopping
    // it this way loses nothing. couchd goes first, so it doesn't log a lost connection.
    public static void Stop(Action<string> log)
    {
        foreach (string path in new[] { CouchdPath, TachyonPath })
        {
            foreach (Process process in Find(path))
            {
                using (process)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                    {
                        log($"Could not stop {path}: {e.Message}");
                    }
                }
            }
        }
    }

    // cmd does the redirect to the log file: the log then keeps working after this UI has gone.
    // Paths are relative to the working directory, so a non-ASCII user name can't get garbled on
    // its way through the C++ programs' narrow command lines.
    public static string BuildCommandLine(string program, string arguments)
    {
        string log = $@"logs\{Path.GetFileNameWithoutExtension(program)}.log";
        // cmd /c strips the outermost pair of quotes, which is why there's an extra pair around it all.
        return $"/c \"\"{program}\" {arguments} > \"{log}\" 2>&1\"";
    }

    private static bool StartOne(string program, string arguments, string workingFolder, Action<string> log)
    {
        List<Process> running = Find(program);
        bool isRunning = running.Count > 0;
        running.ForEach(process => process.Dispose());
        if (isRunning) return true;

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", BuildCommandLine(program, arguments))
        {
            UseShellExecute = false,
            CreateNoWindow = true,  // both are console programs, and a TV shouldn't show their windows
            WorkingDirectory = workingFolder,
        };
        try
        {
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            log($"Could not start {program}: {e.Message}");
            return false;
        }
    }

    // Matches the full path: a separate Tachyon the user runs for something else isn't ours to touch.
    private static List<Process> Find(string path)
    {
        var found = new List<Process>();
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            bool isOurs = false;
            try
            {
                isOurs = string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                // It has already exited, or belongs to another user: not ours.
            }
            if (isOurs) found.Add(process);
            else process.Dispose();
        }
        return found;
    }
}
