using CouchShell.Services;
using CouchShell.ViewModels;

namespace CouchShell.Tests;

public class MainViewModelTests
{
    // Enough of Tachyon for the view model, in memory. Runs everything synchronously.
    private sealed class FakeClient : ICouchClient
    {
        public readonly Dictionary<string, Dictionary<string, string>> Hashes = [];
        public readonly Dictionary<string, List<string>> Lists = [];
        public readonly Dictionary<string, string> Strings = [];
        public readonly List<string> Published = [];
        public long Listeners = 1;
        public bool Connected = true;

        public event Action<bool>? ConnectionChanged;
        public event Action<string, string>? MessageReceived;

        public void Connect() => ConnectionChanged?.Invoke(true);
        public void Disconnect()
        {
            Connected = false;
            ConnectionChanged?.Invoke(false);
        }
        public void Event(string payload) => MessageReceived?.Invoke(MainViewModel.EventChannel, payload);

        public Task<RespValue> SendAsync(params string[] args)
        {
            if (!Connected) throw new CouchException("Not connected.");
            return Task.FromResult(Run(args));
        }

        private static RespValue Bulk(string? text) => text == null ? RespValue.Null : new RespValue(RespKind.Bulk, text);
        private static RespValue Int(long value) => new(RespKind.Integer, Integer: value);
        private static RespValue Array(IEnumerable<string> items) =>
            new(RespKind.Array, Items: items.Select(item => Bulk(item)).ToList());

        private RespValue Run(string[] args)
        {
            switch (args[0])
            {
                case "HSET":
                    if (!Hashes.TryGetValue(args[1], out Dictionary<string, string>? hash)) Hashes[args[1]] = hash = [];
                    for (int i = 2; i + 1 < args.Length; i += 2) hash[args[i]] = args[i + 1];
                    return Int(1);
                case "HGETALL":
                    return Array(Hashes.GetValueOrDefault(args[1], []).SelectMany(pair => new[] { pair.Key, pair.Value }));
                case "RPUSH":
                    if (!Lists.TryGetValue(args[1], out List<string>? list)) Lists[args[1]] = list = [];
                    list.Add(args[2]);
                    return Int(list.Count);
                case "LRANGE":
                    return Array(Lists.GetValueOrDefault(args[1], []));
                case "DEL":
                    Hashes.Remove(args[1]);
                    Lists.Remove(args[1]);
                    Strings.Remove(args[1]);
                    return Int(1);
                case "GET":
                    return Bulk(Strings.GetValueOrDefault(args[1]));
                case "EXISTS":
                    return Int(Strings.ContainsKey(args[1]) ? 1 : 0);
                case "PUBLISH":
                    Published.Add(args[2]);
                    return Int(Listeners);
                default:
                    throw new InvalidOperationException("Unexpected command " + args[0]);
            }
        }
    }

    private sealed class FakeWindow : IWindowService
    {
        public bool IsHidden { get; private set; }
        public int Restores { get; private set; }
        public void Hide() => IsHidden = true;
        public void RestoreAndFocus()
        {
            IsHidden = false;
            Restores++;
        }
    }

    private static readonly AppDefinition Notes = new("notes", "Notes", "apps", "exe", "C:\\notes.exe");
    private static readonly AppDefinition Game = new("game", "Game", "games", "steam", "440", ProcessName: "g.exe");
    private static readonly AppDefinition Video = new("video", "Video", "watch", "web", "https://v.com");

    private readonly FakeClient _client = new();
    private readonly FakeWindow _window = new();
    private readonly MainViewModel _viewModel;
    private readonly List<string?> _focus = [];

    public MainViewModelTests()
    {
        _viewModel = new MainViewModel(_client, [Notes, Game, Video], _window);
        _viewModel.FocusRequested += key => _focus.Add(key);
    }

    private TileViewModel Tile(string key) => _viewModel.Rows.SelectMany(row => row.Tiles).First(tile => tile.Key == key);
    private void Open(string screen) => _viewModel.Tabs.First(tab => tab.Name == screen).Command.Execute(null);
    private List<string> RowTitles() => _viewModel.Rows.Select(row => row.Title).ToList();

    [Fact]
    public void ConnectingPublishesTheCatalogAndShowsItFromTachyon()
    {
        Assert.False(_viewModel.IsConnected);
        _client.Connect();

        Assert.True(_viewModel.IsConnected);
        Assert.Equal(["notes", "game", "video"], _client.Lists["couch:apps"]);
        Assert.Equal("exe", _client.Hashes["app:notes"]["type"]);
        Assert.Equal(["All apps"], RowTitles());
        Assert.Equal(3, _viewModel.Rows[0].Tiles.Count);
    }

    [Fact]
    public void AppRemovedFromTheFileDisappearsFromTachyon()
    {
        _client.Lists["couch:apps"] = ["old"];
        _client.Hashes["app:old"] = new() { ["id"] = "old", ["name"] = "Old" };
        _client.Connect();
        Assert.False(_client.Hashes.ContainsKey("app:old"));
        Assert.DoesNotContain("old", _client.Lists["couch:apps"]);
    }

    [Fact]
    public void ScreensFilterByCategory()
    {
        _client.Connect();
        Open("Games");
        Assert.Equal(["game"], _viewModel.Rows[0].Tiles.Select(tile => tile.Key));
        Open("Watch");
        Assert.Equal(["video"], _viewModel.Rows[0].Tiles.Select(tile => tile.Key));
        Open("Apps");
        Assert.Equal(["notes"], _viewModel.Rows[0].Tiles.Select(tile => tile.Key));
        Assert.True(_viewModel.Tabs.First(tab => tab.Name == "Apps").IsCurrent);
    }

    [Fact]
    public void RecentRowShowsEachKnownAppOnce()
    {
        _client.Lists["couch:recent"] = ["game", "notes", "game", "removed"];
        _client.Connect();
        Assert.Equal(["Recently used", "All apps"], RowTitles());
        Assert.Equal(["game", "notes"], _viewModel.Rows[0].Tiles.Select(tile => tile.Key));
    }

    [Fact]
    public void StartupFocusesTheLastSelectedTile()
    {
        _client.Hashes["couch:settings"] = new() { ["lastTile"] = "game" };
        _client.Connect();
        Assert.Equal("game", _focus.Last());
    }

    [Fact]
    public void FullLaunchCycleHidesThenReturnsToTheSameTile()
    {
        _client.Connect();
        Tile("game").Command.Execute(null);
        Assert.Equal("launch game", _client.Published.Last());
        Assert.Equal("game", _client.Hashes["couch:settings"]["lastTile"]);
        Assert.Equal("Starting Game...", _viewModel.Message);
        Assert.False(_window.IsHidden);

        _client.Event("started game");
        Assert.True(_window.IsHidden);
        Assert.Null(_viewModel.Message);

        _client.Lists["couch:recent"] = ["game"];
        _client.Event("exited game");
        Assert.False(_window.IsHidden);
        Assert.Equal(1, _window.Restores);
        Assert.Equal("game", _focus.Last());
        Assert.Equal(["Recently used", "All apps"], RowTitles());
    }

    [Fact]
    public void LaunchFailureShowsAFriendlyMessageAndStaysVisible()
    {
        _client.Connect();
        _client.Event("launch_failed notes The configured executable could not be found.");
        Assert.Equal("Unable to launch Notes. The configured executable could not be found.", _viewModel.Message);
        Assert.False(_window.IsHidden);
    }

    [Fact]
    public void LaunchWithNoHelperRunningSaysSo()
    {
        _client.Connect();
        _client.Listeners = 0;
        Tile("notes").Command.Execute(null);
        Assert.Contains("background service", _viewModel.Message);
    }

    [Fact]
    public void LaunchWhileDisconnectedSaysSo()
    {
        _client.Connect();
        _client.Disconnect();
        Assert.False(_viewModel.IsConnected);
        Tile("notes").Command.Execute(null);
        Assert.Contains("Not connected", _viewModel.Message);
    }

    [Fact]
    public void UiRestartedMidGameStaysHiddenAndCarriesOn()
    {
        _client.Strings["couch:running"] = "game";
        _client.Strings["couch:alive"] = "1";
        _client.Connect();
        Assert.True(_window.IsHidden);

        _client.Strings.Remove("couch:running");
        _client.Event("exited game");
        Assert.False(_window.IsHidden);
        Assert.Equal("game", _focus.Last());
    }

    [Fact]
    public void StaleRunningKeyFromADeadHelperDoesNotHideTheWindow()
    {
        _client.Strings["couch:running"] = "game";  // no couch:alive
        _client.Connect();
        Assert.False(_window.IsHidden);
    }

    [Fact]
    public async Task MissedExitEventIsCaughtByThePeriodicCheck()
    {
        _client.Connect();
        _client.Strings["couch:running"] = "game";
        _client.Strings["couch:alive"] = "1";
        _client.Event("started game");

        await _viewModel.CheckRunningAppAsync();
        Assert.True(_window.IsHidden);

        _client.Strings.Remove("couch:running");
        await _viewModel.CheckRunningAppAsync();
        Assert.False(_window.IsHidden);
        Assert.Equal("game", _focus.Last());
    }

    [Fact]
    public async Task DeadHelperBringsTheWindowBackAfterSeveralChecks()
    {
        _client.Connect();
        _client.Strings["couch:running"] = "game";
        _client.Event("started game");  // no couch:alive from here on

        await _viewModel.CheckRunningAppAsync();
        await _viewModel.CheckRunningAppAsync();
        Assert.True(_window.IsHidden);
        await _viewModel.CheckRunningAppAsync();
        Assert.False(_window.IsHidden);
        Assert.Contains("Lost track", _viewModel.Message);
    }

    [Fact]
    public async Task LosingTachyonWhileHiddenAlsoBringsTheWindowBack()
    {
        _client.Connect();
        _client.Event("started game");
        _client.Disconnect();

        for (int i = 0; i < 3; i++) await _viewModel.CheckRunningAppAsync();
        Assert.False(_window.IsHidden);
    }

    [Fact]
    public void BrowserChoiceIsSavedAndShown()
    {
        _client.Connect();
        Open("Settings");
        Assert.Equal("Selected", Tile("browser:chrome").Subtitle);

        Tile("browser:edge").Command.Execute(null);
        Assert.Equal("edge", _client.Hashes["couch:settings"]["browser"]);
        Assert.Equal("Selected", Tile("browser:edge").Subtitle);
        Assert.Null(Tile("browser:chrome").Subtitle);
    }

    [Fact]
    public void InterfaceSoundsCycleThroughStylesAndOffAndAreRemembered()
    {
        _client.Connect();
        Assert.Equal("soft", _viewModel.SoundPack);
        Open("Settings");
        Assert.Equal("Soft", Tile("sound:ui").Subtitle);

        Tile("sound:ui").Command.Execute(null);
        Assert.Equal("click", _viewModel.SoundPack);
        Tile("sound:ui").Command.Execute(null);
        Assert.Equal("chime", _viewModel.SoundPack);
        Tile("sound:ui").Command.Execute(null);
        Assert.Equal(MainViewModel.SoundsOff, _viewModel.SoundPack);
        Assert.Equal("Off", Tile("sound:ui").Subtitle);
        Assert.Equal("off", _client.Hashes["couch:settings"]["sounds"]);
        Assert.Equal("sound:ui", _focus.Last());

        var restarted = new MainViewModel(_client, [], _window);
        _client.Connect();
        Assert.Equal(MainViewModel.SoundsOff, restarted.SoundPack);

        Tile("sound:ui").Command.Execute(null);
        Assert.Equal("soft", _viewModel.SoundPack);
    }

    [Fact]
    public void AnOldOrUnknownSoundSettingFallsBackToTheDefault()
    {
        _client.Hashes["couch:settings"] = new() { ["sounds"] = "on" };
        _client.Connect();
        Assert.Equal("soft", _viewModel.SoundPack);
    }

    [Fact]
    public void PointerSpeedPresetsAreSavedAndShown()
    {
        _client.Connect();
        Assert.Equal(60, _viewModel.PointerSpeed);
        Open("Settings");
        Assert.Equal("Selected", Tile("pointer:60").Subtitle);

        Tile("pointer:100").Command.Execute(null);
        Assert.Equal(100, _viewModel.PointerSpeed);
        Assert.Equal("100", _client.Hashes["couch:settings"]["pointerSpeed"]);
        Assert.Equal("Selected", Tile("pointer:100").Subtitle);
        Assert.Equal("60%", Tile("pointer:60").Subtitle);
        Assert.Equal("pointer:100", _focus.Last());
    }

    [Fact]
    public void AdvancedPointerSpeedStepsAndStopsAtTheLimits()
    {
        _client.Connect();
        Open("Settings");
        Tile("pointer:slower").Command.Execute(null);
        Assert.Equal(55, _viewModel.PointerSpeed);
        Assert.Equal("Now 55%", Tile("pointer:slower").Subtitle);

        Tile("pointer:100").Command.Execute(null);
        Tile("pointer:faster").Command.Execute(null);
        Assert.Equal(100, _viewModel.PointerSpeed);  // High is the top

        for (int i = 0; i < 30; i++) Tile("pointer:slower").Command.Execute(null);
        Assert.Equal(5, _viewModel.PointerSpeed);  // never so slow it stops
    }

    [Theory]
    [InlineData("100", 100)]
    [InlineData("45", 45)]
    [InlineData("9999", 100)]
    [InlineData("0", 5)]
    [InlineData("fast", 60)]
    public void SavedPointerSpeedIsRememberedWithinLimits(string saved, int expected)
    {
        _client.Hashes["couch:settings"] = new() { ["pointerSpeed"] = saved };
        _client.Connect();
        Assert.Equal(expected, _viewModel.PointerSpeed);
    }

    [Fact]
    public void TappingViewLeavesAnAppRunningButNotAWebApp()
    {
        _client.Connect();
        _viewModel.SetInstalledApps(Installed);
        string discord = Installed[1].Id;

        _client.Event("started " + discord);
        Assert.True(_viewModel.CanLeaveRunningApp);
        _viewModel.LeaveRunningApp();
        Assert.Equal("leave", _client.Published.Last());

        _client.Event("exited " + discord);
        Assert.False(_window.IsHidden);
        Assert.False(_viewModel.CanLeaveRunningApp);

        _client.Published.Clear();
        _client.Event("started video");
        Assert.False(_viewModel.CanLeaveRunningApp);
        _viewModel.LeaveRunningApp();
        Assert.Empty(_client.Published);
    }

    [Fact]
    public void GamesFromOtherStoresSayWhereTheyAreFrom()
    {
        var viewModel = new MainViewModel(_client,
            [new AppDefinition("riot-valorant", "VALORANT", "games", "exe", "x", Logo: "riot", Source: "Riot Games")], _window);
        _client.Connect();
        TileViewModel tile = viewModel.Rows[0].Tiles[0];
        Assert.Equal("Riot Games", tile.Description);
        Assert.Equal("Play", tile.ActionLabel);
        Assert.NotNull(tile.LogoPath);
    }

    private static readonly List<AppDefinition> Installed =
    [
        InstalledApps.ToApp("Calculator", "calc!App", null),
        InstalledApps.ToApp("Discord", "discord", "icon.png"),
        InstalledApps.ToApp("Google Chrome", "Chrome", null),
    ];

    private SearchKey Key(string label) => _viewModel.SearchKeys.SelectMany(row => row).First(key => key.Label == label);
    private List<string> ResultTitles() => _viewModel.Rows.SelectMany(row => row.Tiles).Select(tile => tile.Title).ToList();

    [Fact]
    public void SearchFiltersAppsAsYouType()
    {
        _client.Connect();
        _viewModel.SetInstalledApps(Installed);
        Open("Search");
        Assert.True(_viewModel.IsSearchScreen);
        Assert.Equal(["Calculator", "Discord", "Google Chrome"], ResultTitles());
        Assert.Equal("key:q", _focus.Last());
        Assert.Equal("Search", _viewModel.FocusedTile?.Title);

        Key("D").Command.Execute(null);
        Key("I").Command.Execute(null);
        Assert.Equal(["Discord"], ResultTitles());
        Assert.Equal("di", _viewModel.FocusedTile?.Title);

        Key("Delete").Command.Execute(null);
        Key("Clear").Command.Execute(null);
        Assert.Equal(3, ResultTitles().Count);

        Key("Z").Command.Execute(null);
        Assert.Empty(ResultTitles());
        Assert.Equal("No apps match that.", _viewModel.EmptyText);
    }

    [Fact]
    public void BackDeletesWhileSearchingThenGoesHome()
    {
        _client.Connect();
        _viewModel.SetInstalledApps(Installed);
        Open("Search");
        Key("C").Command.Execute(null);

        _viewModel.BackCommand.Execute(null);
        Assert.Equal("Search", _viewModel.CurrentScreen);
        Assert.Equal("Search", _viewModel.FocusedTile?.Title);

        _viewModel.BackCommand.Execute(null);
        Assert.Equal("Home", _viewModel.CurrentScreen);
    }

    [Fact]
    public void LeavingSearchForgetsTheText()
    {
        _client.Connect();
        _viewModel.SetInstalledApps(Installed);
        Open("Search");
        Key("Z").Command.Execute(null);
        Open("Home");
        Open("Search");
        Assert.Equal(3, ResultTitles().Count);
    }

    [Fact]
    public void AppFoundBySearchLaunchesAndUsesThePointer()
    {
        var seen = new List<WebControlStyle?>();
        _viewModel.WebControlChanged += style => seen.Add(style);
        _client.Connect();
        _viewModel.SetInstalledApps(Installed);
        Open("Search");

        TileViewModel discord = _viewModel.Rows[0].Tiles.First(tile => tile.Title == "Discord");
        Assert.Equal("icon.png", discord.IconPath);
        Assert.Null(discord.Monogram);
        discord.Command.Execute(null);

        // couchd can only start what it can read about, and this app isn't in the catalog.
        Assert.Equal("shell", _client.Hashes["app:" + discord.Key]["type"]);
        Assert.Equal(@"shell:AppsFolder\discord", _client.Hashes["app:" + discord.Key]["target"]);
        Assert.Equal("launch " + discord.Key, _client.Published.Last());

        _client.Event("started " + discord.Key);
        Assert.Equal(WebControlStyle.Pointer, seen.Last());
    }

    [Fact]
    public void RecentRowCanShowAnAppFoundBySearch()
    {
        AppDefinition found = Installed[1];
        _client.Lists["couch:recent"] = [found.Id];
        _viewModel.SetInstalledApps(Installed);
        _client.Connect();
        Assert.Equal("Recently used", _viewModel.Rows[0].Title);
        Assert.Equal("Discord", _viewModel.Rows[0].Tiles[0].Title);
    }

    [Theory]
    [InlineData("Discord", "com.squirrel.Discord.Discord", true)]
    [InlineData("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true)]
    [InlineData("Uninstall CMake", "Microsoft.AutoGenerated.{BF0E}", false)]
    [InlineData("Python 3.13 Manuals (64-bit)", "C:\\Python313\\Doc\\index.html", false)]
    [InlineData("Steam Support Center", "http://support.steampowered.com/", false)]
    [InlineData("", "x", false)]
    public void TheAppListLeavesOutThingsThatAreNotApps(string name, string path, bool expected)
    {
        Assert.Equal(expected, InstalledApps.IsLaunchable(name, path));
    }

    [Fact]
    public void SearchPutsNamesStartingWithTheTextFirst()
    {
        var apps = new List<AppDefinition> { InstalledApps.ToApp("Audio Mixer", "a", null), InstalledApps.ToApp("Mixer", "b", null) };
        Assert.Equal(["Mixer", "Audio Mixer"], InstalledApps.Search(apps, "mix").Select(app => app.Name));
    }

    [Fact]
    public void AppIdsAreStableAndSafeForCouchd()
    {
        string id = InstalledApps.ToApp("A", @"C:\My Apps\a.exe", null).Id;
        Assert.Equal(id, InstalledApps.ToApp("A", @"C:\My Apps\a.exe", null).Id);
        Assert.Matches("^[a-z0-9_-]{1,64}$", id);
    }

    private static readonly AppDefinition Raid = new("raid", "Raid", "games", "exe", "C:\\bnet.exe", Source: "Battle.net");

    private GuideItem GuideLine(string key) => _viewModel.GuideItems.First(item => item.Key == key);

    [Fact]
    public void GamesListsEveryGameThenEachStore()
    {
        var viewModel = new MainViewModel(_client, [Game, Raid, Notes], _window);
        _client.Connect();
        viewModel.Tabs.First(tab => tab.Name == "Games").Command.Execute(null);

        Assert.Equal(["All games", "Battle.net", "Steam"], viewModel.Rows.Select(row => row.Title));
        Assert.Equal("All games", viewModel.SelectedSection);
        Assert.Equal(["game", "raid"], viewModel.SectionTiles.Select(tile => tile.Key));

        viewModel.ShowSection("Steam");
        Assert.Equal(["game"], viewModel.SectionTiles.Select(tile => tile.Key));

        // Focusing a tile that isn't showing switches to the first section that has it.
        viewModel.RevealTile("raid");
        Assert.Equal("All games", viewModel.SelectedSection);
        viewModel.RevealTile("game");
        Assert.Equal("All games", viewModel.SelectedSection);  // already showing: stays
    }

    [Fact]
    public void GamesFromOneStoreDontRepeatTheList()
    {
        _client.Connect();
        Open("Games");
        Assert.Equal(["All games"], RowTitles());
    }

    [Fact]
    public void SettingsAreGroupedIntoSections()
    {
        _client.Connect();
        Open("Settings");
        Assert.Equal(["Sound", "Controller", "Web apps", "Power"], RowTitles());
        Assert.Equal("Sound", _viewModel.SelectedSection);
    }

    [Fact]
    public void StartMenuOpensOnVolumeAndBackReturnsToTheTile()
    {
        _client.Connect();
        _viewModel.OnTileFocused(Tile("game"));

        _viewModel.GuideCommand.Execute(null);
        Assert.True(_viewModel.IsGuideOpen);
        Assert.False(_viewModel.IsBackgroundEnabled);
        Assert.Equal("guide:volume", _focus.Last());

        _viewModel.BackCommand.Execute(null);
        Assert.False(_viewModel.IsGuideOpen);
        Assert.True(_viewModel.IsBackgroundEnabled);
        Assert.Equal("game", _focus.Last());
    }

    [Fact]
    public void StartMenuShowsAndChangesTheCurrentSettings()
    {
        _client.Hashes["couch:volume"] = new() { ["percent"] = "40", ["muted"] = "0" };
        _client.Connect();
        _viewModel.GuideCommand.Execute(null);
        Assert.Equal("40", GuideLine("guide:volume").Value);
        Assert.Equal("Off", GuideLine("guide:mute").Value);
        Assert.Equal("Soft", GuideLine("guide:sounds").Value);
        Assert.Equal("Medium", GuideLine("guide:pointer").Value);

        _client.Event("volume 55 1");
        Assert.Equal("Muted", GuideLine("guide:volume").Value);
        Assert.Equal("On", GuideLine("guide:mute").Value);
        Assert.False(_viewModel.IsVolumeVisible);  // the menu shows it already

        GuideLine("guide:sounds").Command.Execute(null);
        Assert.Equal("Click", GuideLine("guide:sounds").Value);
        GuideLine("guide:pointer").Command.Execute(null);
        Assert.Equal("High", GuideLine("guide:pointer").Value);
        Assert.Equal("100", _client.Hashes["couch:settings"]["pointerSpeed"]);
        Assert.True(_viewModel.IsGuideOpen);
    }

    [Fact]
    public void PowerFromTheStartMenuConfirmsThenClosesTheMenu()
    {
        _client.Connect();
        _viewModel.OnTileFocused(Tile("notes"));
        _viewModel.GuideCommand.Execute(null);

        GuideLine("guide:sleep").Command.Execute(null);
        Assert.True(_viewModel.IsConfirmOpen);
        Assert.Equal("confirm:no", _focus.Last());
        _viewModel.BackCommand.Execute(null);  // B closes the question, not the menu
        Assert.False(_viewModel.IsConfirmOpen);
        Assert.True(_viewModel.IsGuideOpen);
        Assert.Equal("guide:sleep", _focus.Last());

        GuideLine("guide:sleep").Command.Execute(null);
        _viewModel.ConfirmYesCommand.Execute(null);
        Assert.Equal("power sleep", _client.Published.Last());
        Assert.False(_viewModel.IsGuideOpen);
        Assert.Equal("notes", _focus.Last());
    }

    [Fact]
    public void TheLatestRecentTileIsWideWithItsFullArtwork()
    {
        var art = Game with { Image = "header.jpg", Backdrop = "hero.jpg", TitleImage = "logo.png" };
        var viewModel = new MainViewModel(_client, [art, Notes], _window);
        _client.Lists["couch:recent"] = ["game", "notes"];
        _client.Connect();

        TileViewModel first = viewModel.Rows[0].Tiles[0];
        Assert.True(first.IsWide);
        Assert.Equal("hero.jpg", first.Image);
        Assert.Equal("logo.png", first.TitleImage);
        Assert.False(viewModel.Rows[0].Tiles[1].IsWide);
        Assert.False(viewModel.Rows[1].Tiles[0].IsWide);  // All apps
    }

    [Fact]
    public void TilesSayWhenTheyWereLastPlayed()
    {
        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _client.Lists["couch:sessions"] = [$"game\t{now - 60000}\t{now}"];
        _client.Connect();
        Assert.Equal("Steam game  ·  Last played today", Tile("game").Description);
        Assert.Equal("App", Tile("notes").Description);
    }

    [Theory]
    [InlineData(0, "today")]
    [InlineData(1, "yesterday")]
    [InlineData(3, "3 days ago")]
    public void LastPlayedDaysReadNaturally(int daysAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 11, 3, 15, 0, 0, TimeSpan.FromHours(-5));
        long end = now.AddDays(-daysAgo).ToUnixTimeMilliseconds();
        Assert.Equal(expected, MainViewModel.DescribeDay(end, now));
    }

    [Fact]
    public void LastPlayedOverAWeekAgoGivesTheDate()
    {
        var now = new DateTimeOffset(2026, 11, 3, 15, 0, 0, TimeSpan.FromHours(-5));
        long end = now.AddDays(-12).ToUnixTimeMilliseconds();
        Assert.Equal(now.AddDays(-12).ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture), MainViewModel.DescribeDay(end, now));
    }

    [Fact]
    public void ExitNeedsConfirmationAndSendsNoPowerRequest()
    {
        int exits = 0;
        _viewModel.ExitRequested += () => exits++;
        _client.Connect();
        Open("Settings");

        Tile("power:exit").Command.Execute(null);
        Assert.True(_viewModel.IsConfirmOpen);
        Assert.Equal(0, exits);

        _viewModel.ConfirmYesCommand.Execute(null);
        Assert.Equal(1, exits);
        Assert.DoesNotContain(_client.Published, request => request.StartsWith("power"));
    }

    [Fact]
    public void PowerActionNeedsConfirmationAndStartsOnNo()
    {
        _client.Connect();
        Open("Settings");
        Tile("power:shutdown").Command.Execute(null);

        Assert.True(_viewModel.IsConfirmOpen);
        Assert.Equal("confirm:no", _focus.Last());
        Assert.DoesNotContain(_client.Published, request => request.StartsWith("power"));

        _viewModel.ConfirmNoCommand.Execute(null);
        Assert.False(_viewModel.IsConfirmOpen);
        Assert.Equal("power:shutdown", _focus.Last());
        Assert.DoesNotContain(_client.Published, request => request.StartsWith("power"));

        Tile("power:shutdown").Command.Execute(null);
        _viewModel.ConfirmYesCommand.Execute(null);
        Assert.Equal("power shutdown", _client.Published.Last());
        Assert.False(_viewModel.IsConfirmOpen);
    }

    [Fact]
    public void BackClosesTheConfirmationFirstThenGoesHome()
    {
        _client.Connect();
        Open("Settings");
        Tile("power:sleep").Command.Execute(null);

        _viewModel.BackCommand.Execute(null);
        Assert.False(_viewModel.IsConfirmOpen);
        Assert.Equal("Settings", _viewModel.CurrentScreen);
        Assert.DoesNotContain(_client.Published, request => request.StartsWith("power"));

        _viewModel.BackCommand.Execute(null);
        Assert.Equal("Home", _viewModel.CurrentScreen);
    }

    [Fact]
    public void VolumeEventsUpdateTheOverlayOnlyOnARealChange()
    {
        _client.Connect();
        _client.Event("volume 40 0");
        Assert.Equal(40, _viewModel.VolumePercent);
        Assert.False(_viewModel.IsVolumeVisible);  // the first report isn't a change

        _client.Event("volume 40 0");
        Assert.False(_viewModel.IsVolumeVisible);

        _client.Event("volume 45 0");
        Assert.True(_viewModel.IsVolumeVisible);

        _client.Event("volume 45 1");
        Assert.True(_viewModel.IsMuted);
    }

    [Fact]
    public void VolumeCommandsGoToCouchd()
    {
        _client.Connect();
        _viewModel.VolumeUpCommand.Execute(null);
        _viewModel.VolumeDownCommand.Execute(null);
        _viewModel.MuteCommand.Execute(null);
        Assert.Equal(["volume up", "volume down", "volume mute"], _client.Published);
    }

    [Fact]
    public void HistoryScreenShowsSessionsAndSkipsBadRecords()
    {
        _client.Lists["couch:sessions"] = ["game\t0\t3600000", "garbage", "notes\t0\t1000"];
        _client.Connect();
        Open("History");
        List<TileViewModel> tiles = _viewModel.Rows[0].Tiles.ToList();
        Assert.Equal(["Game", "Notes"], tiles.Select(tile => tile.Title));
        Assert.EndsWith("1 h 0 min", tiles[0].Subtitle);
        Assert.EndsWith("under a minute", tiles[1].Subtitle);
    }

    [Fact]
    public void EmptyScreensExplainThemselves()
    {
        Assert.Equal("Connecting...", _viewModel.EmptyText);

        _client.Connect();
        Assert.Null(_viewModel.EmptyText);

        Open("History");
        Assert.Equal("Nothing played yet.", _viewModel.EmptyText);
    }

    [Fact]
    public void AppScreenWithNothingInItSaysWhereToAddApps()
    {
        var viewModel = new MainViewModel(_client, [], _window);
        _client.Connect();
        Assert.Contains("apps.json", viewModel.EmptyText);
    }

    [Fact]
    public void TilesWithoutArtworkShowTheirFirstLetter()
    {
        _client.Connect();
        Assert.Equal("G", Tile("game").Monogram);
        Assert.Equal("Steam game", Tile("game").Description);
        Assert.Equal("Web app", Tile("video").Description);
    }

    [Fact]
    public void KnownLogosAndCustomColoursReachTheTile()
    {
        var viewModel = new MainViewModel(_client,
        [
            new AppDefinition("yt", "YouTube", "watch", "web", "https://y.com", Logo: "youtube"),
            new AppDefinition("mine", "Mine", "watch", "web", "https://m.com", Color: "#123456"),
            new AppDefinition("art", "Art", "games", "steam", "1", Image: "a.jpg", Logo: "youtube", Backdrop: "b.jpg"),
            new AppDefinition("web", "Web", "apps", "web", "https://w.com", Logo: "no-such-logo", Window: "browser"),
        ], _window);
        _client.Connect();
        List<TileViewModel> tiles = viewModel.Rows[0].Tiles.ToList();

        Assert.Equal(BrandLogos.Find("youtube")!.Path, tiles[0].LogoPath);
        Assert.Null(tiles[0].Monogram);
        Assert.Equal("#123456", tiles[1].Accent);
        Assert.Equal("M", tiles[1].Monogram);
        Assert.Null(tiles[2].LogoPath);  // real artwork wins over a logo
        Assert.Equal("b.jpg", tiles[2].Backdrop);
        Assert.Equal("W", tiles[3].Monogram);  // an unknown logo name falls back instead of failing
        Assert.Equal("Web browser", tiles[3].Description);
    }

    [Fact]
    public void WaitingForSteamHidesTheUiAndLetsTheControllerUseItsDialog()
    {
        var seen = new List<WebControlStyle?>();
        _viewModel.WebControlChanged += style => seen.Add(style);
        _client.Connect();

        _client.Event("launching game");
        Assert.True(_window.IsHidden);
        Assert.Equal(WebControlStyle.WaitingForLaunch, seen.Last());

        _client.Event("started game");
        Assert.True(_window.IsHidden);
        Assert.Null(seen.Last());  // the game reads the controller itself from here

        _client.Event("exited game");
        Assert.False(_window.IsHidden);
        Assert.Equal("game", _focus.Last());
    }

    [Fact]
    public void CancellingALaunchComesBackToTheSameTile()
    {
        var seen = new List<WebControlStyle?>();
        _viewModel.WebControlChanged += style => seen.Add(style);
        _client.Connect();
        _client.Event("launching game");

        _client.Event("launch_cancelled game");
        Assert.False(_window.IsHidden);
        Assert.Null(seen.Last());
        Assert.Equal("game", _focus.Last());
    }

    [Fact]
    public void LaunchThatTimesOutComesBackAndSaysWhy()
    {
        _client.Connect();
        _client.Event("launching game");
        _client.Event("launch_failed game It did not start in time.");
        Assert.False(_window.IsHidden);
        Assert.Equal("Unable to launch Game. It did not start in time.", _viewModel.Message);
    }

    [Fact]
    public async Task PeriodicCheckLeavesAWaitingLaunchAlone()
    {
        _client.Connect();
        _client.Strings["couch:alive"] = "1";
        _client.Event("launching game");  // couch:running isn't set until the game starts

        await _viewModel.CheckRunningAppAsync();
        Assert.True(_window.IsHidden);
    }

    [Fact]
    public void RefusedLaunchWhileAnotherRunsDoesNotDisturbIt()
    {
        var seen = new List<WebControlStyle?>();
        _client.Connect();
        _client.Event("started video");
        _viewModel.WebControlChanged += style => seen.Add(style);

        _client.Event("launch_failed notes Another app is already running.");
        Assert.True(_window.IsHidden);
        Assert.Empty(seen);
    }

    [Fact]
    public void MovingOntoATabShowsItsContentWithoutMovingFocus()
    {
        _client.Connect();
        _focus.Clear();

        _viewModel.ShowScreen("Games");

        Assert.Equal("Games", _viewModel.CurrentScreen);
        Assert.Equal(["game"], _viewModel.Rows[0].Tiles.Select(tile => tile.Key));
        Assert.True(_viewModel.Tabs.First(tab => tab.Name == "Games").IsCurrent);
        Assert.Equal("Game", _viewModel.FocusedTile?.Title);
        Assert.Empty(_focus);  // focus stays on the tab
    }

    [Fact]
    public void SelectingATabThenMovesIntoIt()
    {
        _client.Connect();
        _viewModel.ShowScreen("Games");
        _focus.Clear();

        Open("Games");
        Assert.Equal([null], _focus);
    }

    [Fact]
    public void PreviewFollowsFocusAndClearsOnAnEmptyScreen()
    {
        _client.Connect();
        _viewModel.OnTileFocused(Tile("game"));
        Assert.Equal("Game", _viewModel.FocusedTile?.Title);

        Open("History");
        Assert.Null(_viewModel.FocusedTile);
    }

    [Fact]
    public void ControllerDrivesWebAppsButNeverGames()
    {
        var seen = new List<WebControlStyle?>();
        _viewModel.WebControlChanged += style => seen.Add(style);
        _client.Connect();

        _client.Event("started video");
        Assert.Equal(WebControlStyle.Pointer, seen.Last());
        _client.Event("exited video");
        Assert.Null(seen.Last());

        _client.Event("started game");
        Assert.Null(seen.Last());
        _client.Event("exited game");
        Assert.Null(seen.Last());
    }

    [Fact]
    public async Task LosingTheAppAlsoHandsTheControllerBack()
    {
        var seen = new List<WebControlStyle?>();
        _viewModel.WebControlChanged += style => seen.Add(style);
        _client.Connect();
        _client.Event("started video");
        Assert.Equal(WebControlStyle.Pointer, seen.Last());

        _client.Disconnect();
        for (int i = 0; i < 3; i++) await _viewModel.CheckRunningAppAsync();
        Assert.Null(seen.Last());
    }

    [Fact]
    public void CloseAndVolumeGoToCouchd()
    {
        _client.Connect();
        _viewModel.CloseRunningApp();
        _viewModel.ChangeVolume(NavAction.VolumeUp);
        _viewModel.ChangeVolume(NavAction.VolumeDown);
        Assert.Equal(["close", "volume up", "volume down"], _client.Published);
    }

    [Fact]
    public void PowerFailureIsShown()
    {
        _client.Connect();
        _client.Event("power_failed Windows refused the request.");
        Assert.Equal("That didn't work. Windows refused the request.", _viewModel.Message);
    }

    [Fact]
    public void UnknownEventsAreIgnored()
    {
        _client.Connect();
        _client.Event("");
        _client.Event("something new");
        _client.Event("volume abc 0");
        Assert.Null(_viewModel.Message);
    }
}
