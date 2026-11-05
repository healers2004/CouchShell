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

    [Theory]
    [InlineData("100", 100)]
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
}
