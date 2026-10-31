using CouchShell.Services;

namespace CouchShell.Tests;

public class GameLibrariesTests
{
    private const string RiotUninstall = "\"C:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --uninstall-product=Riot_Client --uninstall-patchline=";
    private static readonly UninstallEntry RiotClient = new("Riot Game Riot_Client.", "Riot Client ", "C:/Riot Games/Riot Client", RiotUninstall);
    private static readonly UninstallEntry BattleNet = new("Battle.net", "Battle.net", "C:\\Program Files (x86)\\Battle.net",
        "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=battle.net --displayname=\"Battle.net\"");

    [Fact]
    public void FindsARiotGameAndHowToLaunchIt()
    {
        var game = Assert.Single(GameLibraries.ParseRiot(
        [
            RiotClient,
            new UninstallEntry("Riot Game valorant.live", "VALORANT", "C:/Riot Games/VALORANT/live", "x"),
        ]));
        Assert.Equal("riot-valorant", game.Id);
        Assert.Equal("VALORANT", game.Name);
        Assert.Equal("C:\\Riot Games\\Riot Client\\RiotClientServices.exe", game.Target);
        Assert.Equal("--launch-product=valorant --launch-patchline=live", game.Args);
        Assert.Equal("C:\\Riot Games\\VALORANT\\live", game.InstallFolder);
        Assert.Null(game.WebControl);  // games read the controller themselves
    }

    [Fact]
    public void TheClientAloneAndTestServersAreNotGames()
    {
        Assert.Empty(GameLibraries.ParseRiot([RiotClient]));
        Assert.Empty(GameLibraries.ParseRiot(
        [
            RiotClient,
            new UninstallEntry("Riot Game valorant.pbe", "VALORANT PBE", "C:/x", "x"),
        ]));
    }

    [Fact]
    public void RiotGameWithoutTheClientCannotBeLaunched()
    {
        Assert.Empty(GameLibraries.ParseRiot([new UninstallEntry("Riot Game valorant.live", "VALORANT", "C:/x", "x")]));
    }

    [Fact]
    public void FindsABattleNetGameWithItsLaunchCode()
    {
        var game = Assert.Single(GameLibraries.ParseBattleNet(
        [
            BattleNet,
            new UninstallEntry("Overwatch", "Overwatch", "C:\\Games\\Overwatch",
                "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=prometheus --displayname=\"Overwatch\""),
        ]));
        Assert.Equal("battlenet-prometheus", game.Id);
        Assert.Equal("--exec=\"launch Pro\"", game.Args);
        Assert.EndsWith("Battle.net.exe", game.Target);
        Assert.Equal("C:\\Games\\Overwatch", game.InstallFolder);
    }

    [Fact]
    public void BattleNetGameWithAnUnknownCodeOpensItsPage()
    {
        var game = Assert.Single(GameLibraries.ParseBattleNet(
        [
            BattleNet,
            new UninstallEntry("New", "New Game", "C:\\Games\\New", "\"x\\Blizzard Uninstaller.exe\" --uid=newgame"),
        ]));
        Assert.Equal("--game=newgame", game.Args);
    }

    [Fact]
    public void BattleNetItselfAndOtherProgramsAreNotGames()
    {
        Assert.Empty(GameLibraries.ParseBattleNet([BattleNet, new UninstallEntry("Notepad++", "Notepad++", "C:\\n", "uninstall.exe")]));
    }

    [Fact]
    public void ReadsAnEpicManifest()
    {
        AppDefinition? game = GameLibraries.ParseEpicManifest("""
            { "DisplayName": "Rocket League", "AppName": "Sugar", "CatalogNamespace": "9773", "CatalogItemId": "530c",
              "InstallLocation": "C:/Epic/rocketleague", "AppCategories": ["public", "games", "applications"] }
            """);
        Assert.NotNull(game);
        Assert.Equal("epic-sugar", game.Id);
        Assert.Equal("uri", game.Type);
        Assert.Equal("com.epicgames.launcher://apps/9773%3A530c%3ASugar?action=launch&silent=true", game.Target);
        Assert.Equal("C:\\Epic\\rocketleague", game.InstallFolder);
    }

    [Theory]
    [InlineData("""{ "DisplayName": "Half done", "AppName": "a", "CatalogNamespace": "b", "CatalogItemId": "c", "InstallLocation": "d", "bIsIncompleteInstall": true }""")]
    [InlineData("""{ "DisplayName": "Unreal Engine", "AppName": "a", "CatalogNamespace": "b", "CatalogItemId": "c", "InstallLocation": "d", "AppCategories": ["engines"] }""")]
    [InlineData("""{ "AppName": "no name" }""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void SkipsEpicManifestsThatAreNotPlayableGames(string json)
    {
        Assert.Null(GameLibraries.ParseEpicManifest(json));
    }

    [Fact]
    public void LauncherTilesUseThePointerAndTrackTheirWindows()
    {
        List<AppDefinition> launchers = GameLibraries.ParseLaunchers(
        [
            BattleNet,
            RiotClient,
            new UninstallEntry("{BC0E}", "Epic Games Launcher", "C:\\Program Files (x86)\\Epic Games\\", "MsiExec.exe"),
        ]);
        Assert.Equal(["Battle.net", "Riot Client", "Epic Games"], launchers.Select(app => app.Name));
        Assert.All(launchers, app =>
        {
            Assert.Equal(WebControlStyle.Pointer, app.WebControl);
            Assert.Equal("window", app.Watch);
            Assert.Equal("apps", app.Category);
        });
        Assert.EndsWith("EpicGamesLauncher.exe", launchers[2].Target);
    }

    [Fact]
    public void NoLaunchersInstalledGivesNoTiles()
    {
        Assert.Empty(GameLibraries.ParseLaunchers([]));
    }
}
