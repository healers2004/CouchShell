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

    [Theory]
    [InlineData(30, true)]    // a faint shadow box: cleared
    [InlineData(255, false)]  // solid edges are part of the picture: kept
    public void IconsLoseOnlyAFaintFrame(byte edgeAlpha, bool cleared)
    {
        const int size = 16;
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < size; i++)
        {
            pixels[i * 4 + 3] = edgeAlpha;  // the top row
        }
        pixels[(8 * size + 8) * 4 + 3] = 255;  // something solid in the middle
        var icon = System.Windows.Media.Imaging.BitmapSource.Create(size, size, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, size * 4);

        var result = new byte[pixels.Length];
        InstalledApps.WithoutFaintFrame(icon).CopyPixels(result, size * 4, 0);

        Assert.Equal(cleared ? 0 : edgeAlpha, result[3]);
        Assert.Equal(255, result[(8 * size + 8) * 4 + 3]);
    }

    [Theory]
    [InlineData("\"C:\\Games\\x.exe\",0", "C:\\Games\\x.exe")]
    [InlineData("C:\\Launcher\\launcher.exe", null)]  // only the launcher's own icon
    [InlineData("C:\\Games\\x.ico", null)]
    public void GameProgramReadsTheDisplayIcon(string? displayIcon, string? expected)
    {
        Assert.Equal(expected, GameLibraries.GameProgram(displayIcon, "C:\\Launcher\\launcher.exe"));
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
    public void SkipsEpicManifestsThatAreNotPlayableGames(string json)
    {
        Assert.Null(GameLibraries.ParseEpicManifest(json));
    }
}
