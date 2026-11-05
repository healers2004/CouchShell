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
}
