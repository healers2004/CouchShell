using CouchShell.Services;
using Xunit;

namespace CouchShell.Tests;

public class BackendHostTests
{
    [Fact]
    public void CommandLineQuotesAPathWithSpaces()
    {
        string line = BackendHost.BuildCommandLine(@"C:\Users\A B\CouchShell\backend\tachyon.exe", "--dir data");

        Assert.Equal(@"/c """"C:\Users\A B\CouchShell\backend\tachyon.exe"" --dir data > ""logs\tachyon.log"" 2>&1""", line);
    }

    [Fact]
    public void EachProgramGetsItsOwnLog()
    {
        Assert.Contains(@"logs\couchd.log", BackendHost.BuildCommandLine(@"C:\app\backend\couchd.exe", ""));
    }

    [Fact]
    public void StartDoesNothingWithoutABackendFolder()
    {
        // The test program has no backend folder next to it, like a build from source.
        var logged = new List<string>();

        Assert.True(BackendHost.Start(logged.Add));
        Assert.Empty(logged);
    }

    [Fact]
    public void CouchShellLeavesItselfOutOfSearch()
    {
        Assert.False(InstalledApps.IsLaunchable("CouchShell", @"C:\app\CouchShell.exe"));
        Assert.True(InstalledApps.IsLaunchable("Couch Potato", @"C:\app\potato.exe"));
    }
}
