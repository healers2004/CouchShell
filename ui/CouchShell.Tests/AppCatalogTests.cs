using CouchShell.Services;

namespace CouchShell.Tests;

public class AppCatalogTests
{
    private readonly List<string> _log = [];

    private List<AppDefinition> Parse(string json) => AppCatalog.Parse(json, "", _log.Add);

    [Fact]
    public void ReadsEveryType()
    {
        List<AppDefinition> apps = Parse("""
            { "apps": [
              { "id": "a", "name": "A", "category": "apps", "type": "exe", "target": "C:\\a.exe", "args": "-x" },
              { "id": "b", "name": "B", "category": "watch", "type": "web", "target": "https://b.com" },
              { "id": "c", "name": "C", "category": "games", "type": "steam", "target": "440", "processName": "c.exe" },
            ] }
            """);
        Assert.Equal(["a", "b", "c"], apps.Select(app => app.Id));
        Assert.Equal("-x", apps[0].Args);
        Assert.Empty(_log);
    }

    [Theory]
    [InlineData("""{ "name": "No id", "type": "exe", "target": "x" }""")]
    [InlineData("""{ "id": "Bad Id", "name": "N", "type": "exe", "target": "x" }""")]
    [InlineData("""{ "id": "../x", "name": "N", "type": "exe", "target": "x" }""")]
    [InlineData("""{ "id": "x", "type": "exe", "target": "x" }""")]
    [InlineData("""{ "id": "x", "name": "N", "type": "exe" }""")]
    [InlineData("""{ "id": "x", "name": "N", "type": "alien", "target": "x" }""")]
    [InlineData("""{ "id": "x", "name": "N", "category": "toys", "type": "exe", "target": "x" }""")]
    [InlineData("""{ "id": "x", "name": "N", "type": "web", "target": "file:///C:/secret" }""")]
    [InlineData("""{ "id": "x", "name": "N", "type": "shell", "target": "C:\\\\evil.exe" }""")]
    [InlineData("""{ "id": "x", "name": "N", "type": "web", "target": "https://a.com", "controller": "psychic" }""")]
    [InlineData("""{ "id": "x", "name": "N", "type": "steam", "target": "abc", "processName": "x.exe" }""")]
    [InlineData("42")]
    public void BadEntryIsLoggedAndSkippedWithoutLosingTheOthers(string badEntry)
    {
        List<AppDefinition> apps = Parse($$"""
            { "apps": [ {{badEntry}}, { "id": "good", "name": "Good", "type": "exe", "target": "C:\\g.exe" } ] }
            """);
        Assert.Equal(["good"], apps.Select(app => app.Id));
        Assert.Single(_log);
    }

    [Fact]
    public void DuplicateIdKeepsTheFirst()
    {
        List<AppDefinition> apps = Parse("""
            { "apps": [
              { "id": "a", "name": "First", "type": "exe", "target": "x" },
              { "id": "a", "name": "Second", "type": "exe", "target": "y" }
            ] }
            """);
        Assert.Equal("First", Assert.Single(apps).Name);
        Assert.Contains("duplicate", Assert.Single(_log));
    }

    [Fact]
    public void MissingImageIsNotAnError()
    {
        List<AppDefinition> apps = Parse("""
            { "apps": [ { "id": "a", "name": "A", "type": "exe", "target": "x", "image": "no-such-file.png" } ] }
            """);
        Assert.Null(Assert.Single(apps).Image);
        Assert.Empty(_log);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "apps": 5 }""")]
    public void BrokenFileGivesNoAppsAndALogLine(string json)
    {
        Assert.Empty(Parse(json));
        Assert.Single(_log);
    }

    [Fact]
    public void MissingFileGivesNoAppsAndALogLine()
    {
        Assert.Empty(AppCatalog.LoadFile("C:\\does\\not\\exist\\apps.json", _log.Add));
        Assert.Single(_log);
    }

    [Fact]
    public void AppThatRequiresAMissingFileIsNotShown()
    {
        var missing = new AppDefinition("a", "A", "apps", "shell", @"shell:AppsFolder\a", Requires: @"C:\does\not\exist.exe");
        var present = missing with { Requires = @"%SystemRoot%\System32\cmd.exe" };
        Assert.False(AppCatalog.IsInstalled(missing));
        Assert.True(AppCatalog.IsInstalled(present));
    }

    [Fact]
    public void BadColourIsIgnoredAndTheAppKept()
    {
        List<AppDefinition> apps = Parse("""
            { "apps": [ { "id": "a", "name": "A", "type": "exe", "target": "x", "color": "red; evil" } ] }
            """);
        Assert.Null(Assert.Single(apps).Color);
    }

    [Theory]
    [InlineData("exe", "C:\\does\\not\\exist.exe", false)]
    [InlineData("exe", "%SystemRoot%\\System32\\cmd.exe", true)]
    [InlineData("web", "https://a.com", true)]
    [InlineData("steam", "440", true)]
    public void OnlyMissingProgramsCountAsNotInstalled(string type, string target, bool expected)
    {
        Assert.Equal(expected, AppCatalog.IsInstalled(new AppDefinition("a", "A", "apps", type, target)));
    }

    [Fact]
    public void MergeKeepsTheFirstOfEachIdInOrder()
    {
        var mine = new AppDefinition("a", "Mine", "apps", "exe", "x");
        var builtIn = new AppDefinition("a", "Built in", "apps", "exe", "y");
        var other = new AppDefinition("b", "Other", "apps", "exe", "z");
        Assert.Equal([mine, other], AppCatalog.Merge([mine], [builtIn, other]));
    }

    [Theory]
    [InlineData("web", null, WebControlStyle.Pointer)]
    [InlineData("web", "keys", WebControlStyle.Keys)]
    [InlineData("web", "pointer", WebControlStyle.Pointer)]
    [InlineData("steam", null, null)]
    [InlineData("exe", null, null)]
    [InlineData("exe", "pointer", WebControlStyle.Pointer)]
    public void OnlyWebAppsGetControllerTranslation(string type, string? controller, WebControlStyle? expected)
    {
        Assert.Equal(expected, new AppDefinition("a", "A", "apps", type, "x", Controller: controller).WebControl);
    }

    [Fact]
    public void HashFieldsRoundTrip()
    {
        var app = new AppDefinition("a", "A", "games", "steam", "440", ProcessName: "a.exe");
        List<string> fields = app.ToHashFields().ToList();
        var hash = new Dictionary<string, string>();
        for (int i = 0; i < fields.Count; i += 2) hash[fields[i]] = fields[i + 1];
        Assert.Equal(app, AppDefinition.FromHash(hash));
        Assert.DoesNotContain("args", fields);
    }
}
