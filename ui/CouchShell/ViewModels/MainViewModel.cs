using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CouchShell.Services;

namespace CouchShell.ViewModels;

// State behind the main window. Everything shared lives in Tachyon; this class reads it, shows it,
// and turns selections into requests for couchd. It knows nothing about WPF controls.
public class MainViewModel : ViewModelBase
{
    public const string EventChannel = "couch:events";
    private const string RequestChannel = "couch:requests";
    private const string AppsKey = "couch:apps";
    private const string SettingsKey = "couch:settings";
    private const string RecentKey = "couch:recent";
    private const string SessionsKey = "couch:sessions";
    private const string VolumeKey = "couch:volume";
    private const string RunningKey = "couch:running";
    private const string AliveKey = "couch:alive";

    private const string HomeScreen = "Home";
    private const string GamesScreen = "Games";
    private const string WatchScreen = "Watch";
    private const string AppsScreen = "Apps";
    private const string SearchScreen = "Search";
    private const string HistoryScreen = "History";
    private const string SettingsScreen = "Settings";

    public const string SoundsOff = "off";
    private const string DefaultSoundPack = "soft";
    private static readonly (string Id, string Label)[] SoundPacks =
        [("soft", "Soft"), ("click", "Click"), ("chime", "Chime"), (SoundsOff, "Off")];

    private const int SlowPointer = 30;
    private const int MediumPointer = 60;
    private const int HighPointer = 100;  // anything faster is too fast to aim with
    private const int PointerStep = 5;

    private const int RecentTiles = 6;
    private const int SearchResults = 40;
    private const string FirstSearchKey = "key:q";
    private const int HistoryTiles = 30;
    // How many checks in a row (one every 2 s) may fail before the UI comes back by itself, so
    // nobody is stranded behind a hidden window if couchd or Tachyon dies.
    private const int MaxMissedChecks = 3;
    private static readonly TimeSpan VolumeOverlayTime = TimeSpan.FromSeconds(2);

    private readonly ICouchClient _client;
    private readonly IReadOnlyList<AppDefinition> _catalog;
    private readonly IWindowService _window;

    private List<AppDefinition> _apps = [];
    private List<string> _recent = [];
    private List<string> _sessions = [];
    private string _browser = "chrome";
    private string? _lastTile;
    private string? _runningAppId;
    private bool _isWaitingForLaunch;  // Steam is updating or showing a dialog, not running it yet
    private TileViewModel? _focusedTile;
    private List<AppDefinition> _installedApps = [];
    private string _searchText = "";
    private int _missedChecks;
    private bool _focusPending = true;

    private string _currentScreen = HomeScreen;
    private bool _isConnected;
    private string? _message;
    private string? _emptyText = "Connecting...";
    private int _volumePercent;
    private bool _isMuted;
    private bool _volumeKnown;
    private bool _isVolumeVisible;
    private int _volumeOverlayVersion;
    private string? _confirmText;
    private Func<Task>? _confirmAction;
    private string? _confirmReturnKey;
    private string? _selectedSection;
    private bool _isGuideOpen;
    private string? _guideReturnKey;

    public MainViewModel(ICouchClient client, IReadOnlyList<AppDefinition> catalog, IWindowService window)
    {
        _client = client;
        _catalog = catalog;
        _window = window;

        string[] screens = [HomeScreen, GamesScreen, WatchScreen, AppsScreen, SearchScreen, HistoryScreen, SettingsScreen];
        Tabs = screens.Select(name => new TabViewModel(name, new RelayCommand(_ => OpenScreen(name)))).ToList();
        Tabs[0].IsCurrent = true;

        SearchKeys = BuildSearchKeys();

        BackCommand = new RelayCommand(_ => Back());
        ConfirmYesCommand = new RelayCommand(_ => Guard(ConfirmYesAsync));
        ConfirmNoCommand = new RelayCommand(_ => CloseConfirm());
        VolumeDownCommand = new RelayCommand(_ => Guard(() => RequestAsync("volume down")));
        VolumeUpCommand = new RelayCommand(_ => Guard(() => RequestAsync("volume up")));
        MuteCommand = new RelayCommand(_ => Guard(() => RequestAsync("volume mute")));
        GuideCommand = new RelayCommand(_ => ToggleGuide());
        GuideItems = BuildGuide();

        client.ConnectionChanged += connected => Guard(() => OnConnectionChangedAsync(connected));
        client.MessageReceived += (channel, payload) =>
        {
            if (channel == EventChannel) Guard(() => OnEventAsync(payload));
        };
    }

    // The running app's id. Changing it also tells the view how the controller should behave, so
    // there's no way to leave the controller driving a web app that has gone.
    private string? _runningApp
    {
        get => _runningAppId;
        set
        {
            _runningAppId = value;
            WebControlStyle? style = value != null && _isWaitingForLaunch
                ? WebControlStyle.WaitingForLaunch
                : FindApp(value)?.WebControl;
            WebControlChanged?.Invoke(style);
        }
    }

    // Null when the controller should go back to CouchShell's menus, or to a game's own handling.
    public event Action<WebControlStyle?>? WebControlChanged;

    // Comes back to CouchShell with the app still running.
    public void LeaveRunningApp()
    {
        if (CanLeaveRunningApp) Guard(() => RequestAsync("leave"));
    }

    // Asks couchd to close the running app.
    public void CloseRunningApp() => Guard(() => RequestAsync("close"));

    // A volume action from the controller while a web app runs.
    public void ChangeVolume(NavAction action) => Guard(() => RequestAsync(action == NavAction.VolumeUp ? "volume up" : "volume down"));

    public IReadOnlyList<TabViewModel> Tabs { get; }
    public IReadOnlyList<IReadOnlyList<SearchKey>> SearchKeys { get; }
    public bool IsSearchScreen => CurrentScreen == SearchScreen;
    public ObservableCollection<TileRow> Rows { get; } = [];

    public ICommand BackCommand { get; }
    public ICommand ConfirmYesCommand { get; }
    public ICommand ConfirmNoCommand { get; }
    public ICommand VolumeDownCommand { get; }
    public ICommand VolumeUpCommand { get; }
    public ICommand MuteCommand { get; }
    public ICommand GuideCommand { get; }

    // Games and Settings: a list of sections on the left, and the chosen one's contents.
    public bool IsSectionScreen => CurrentScreen is GamesScreen or SettingsScreen;
    public bool IsGamesScreen => CurrentScreen == GamesScreen;
    public bool IsSettingsScreen => CurrentScreen == SettingsScreen;

    // The section whose contents are showing: one of the rows' titles.
    public string? SelectedSection
    {
        get => _selectedSection;
        private set
        {
            _selectedSection = value;
            Notify(nameof(SelectedSection));
            Notify(nameof(SectionTiles));
        }
    }

    public IReadOnlyList<TileViewModel> SectionTiles => Rows.FirstOrDefault(row => row.Title == SelectedSection)?.Tiles ?? [];

    // Moving onto a section shows its contents, like moving onto a tab.
    public void ShowSection(string title)
    {
        if (title != SelectedSection && Rows.Any(row => row.Title == title)) SelectedSection = title;
    }

    // Before focusing a tile by key: if it's in another section, show that section.
    public void RevealTile(string? key)
    {
        if (key == null || !IsSectionScreen || SectionTiles.Any(tile => tile.Key == key)) return;
        TileRow? row = Rows.FirstOrDefault(candidate => candidate.Tiles.Any(tile => tile.Key == key));
        if (row != null) SelectedSection = row.Title;
    }

    // The Start menu: volume, sounds, pointer speed and power, over any screen.
    public IReadOnlyList<GuideItem> GuideItems { get; }

    public bool IsGuideOpen
    {
        get => _isGuideOpen;
        private set
        {
            Set(ref _isGuideOpen, value);
            Notify(nameof(IsBackgroundEnabled));
        }
    }

    // False while the menu or a confirmation is open, so focus can't wander behind them.
    public bool IsBackgroundEnabled => !IsGuideOpen && !IsConfirmOpen;

    // Asks the view to focus a tile by key. Null means the first tile on the screen.
    public event Action<string?>? FocusRequested;

    // The user chose to exit. The app owns its own lifetime, so it does the closing.
    public event Action? ExitRequested;

    // What the preview area describes. Tracks focus, which only the view knows about.
    public TileViewModel? FocusedTile
    {
        get => _focusedTile;
        private set => Set(ref _focusedTile, value);
    }

    public void OnTileFocused(TileViewModel tile) => FocusedTile = tile;

    public void OnSearchKeyFocused() => FocusedTile = SearchPreview();

    // Arrives a little after startup, because reading every app and icon takes a moment.
    public void SetInstalledApps(List<AppDefinition> apps)
    {
        _installedApps = apps;
        if (!IsSearchScreen) return;
        string? focused = FocusedTile?.Key;
        RebuildRows();
        if (focused != null && focused != SearchPreviewKey) FocusRequested?.Invoke(focused);
    }

    private const string SearchPreviewKey = "search:preview";

    private TileViewModel SearchPreview() =>
        new(SearchPreviewKey, _searchText.Length == 0 ? "Search" : _searchText, null, null, new RelayCommand(_ => { }),
            Description: _searchText.Length == 0 ? "Find any app on this PC" : null);

    private AppDefinition? FindApp(string? id) =>
        _apps.Find(app => app.Id == id) ?? _installedApps.Find(app => app.Id == id);

    private List<IReadOnlyList<SearchKey>> BuildSearchKeys()
    {
        string[] rows = ["1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm"];
        var keys = rows.Select(row => (IReadOnlyList<SearchKey>)row
            .Select(c => new SearchKey(c.ToString().ToUpperInvariant(), "key:" + c, new RelayCommand(_ => TypeSearch(c.ToString()))))
            .ToList()).ToList();
        keys.Add(
        [
            new SearchKey("Space", "key:space", new RelayCommand(_ => TypeSearch(" ")), 260),
            new SearchKey("Delete", "key:delete", new RelayCommand(_ => DeleteSearch()), 260),
            new SearchKey("Clear", "key:clear", new RelayCommand(_ => SetSearch("")), 260),
        ]);
        return keys;
    }

    private void TypeSearch(string text) => SetSearch(_searchText + text);

    private void DeleteSearch()
    {
        if (_searchText.Length > 0) SetSearch(_searchText[..^1]);
    }

    private void SetSearch(string text)
    {
        _searchText = text;
        RebuildRows();
        FocusedTile = SearchPreview();
    }

    public string CurrentScreen
    {
        get => _currentScreen;
        private set
        {
            Set(ref _currentScreen, value);
            Notify(nameof(IsSearchScreen));
            Notify(nameof(IsSectionScreen));
            Notify(nameof(IsGamesScreen));
            Notify(nameof(IsSettingsScreen));
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            Set(ref _isConnected, value);
            UpdateEmptyText();
        }
    }

    // Shown instead of tiles when the screen has none. Null when there are tiles.
    public string? EmptyText
    {
        get => _emptyText;
        private set => Set(ref _emptyText, value);
    }

    // A short message for the user, e.g. why a launch failed.
    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public int VolumePercent
    {
        get => _volumePercent;
        private set => Set(ref _volumePercent, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set => Set(ref _isMuted, value);
    }

    public bool IsVolumeVisible
    {
        get => _isVolumeVisible;
        private set => Set(ref _isVolumeVisible, value);
    }

    public string? ConfirmText
    {
        get => _confirmText;
        private set
        {
            Set(ref _confirmText, value);
            IsConfirmOpen = value != null;
        }
    }

    private bool _isConfirmOpen;

    public bool IsConfirmOpen
    {
        get => _isConfirmOpen;
        private set
        {
            Set(ref _isConfirmOpen, value);
            Notify(nameof(IsBackgroundEnabled));
        }
    }

    // Which set of interface sounds to play, or SoundsOff.
    public string SoundPack { get; private set; } = DefaultSoundPack;

    // How fast the left stick moves the pointer, as a percentage of the fastest setting.
    public int PointerSpeed { get; private set; } = MediumPointer;

    // Things like music can carry on behind CouchShell. Not web apps: they share one browser, and
    // a second site would hand off to the browser that's still open.
    // What has a window up while a launch waits: the game's launcher, or the app itself.
    public string? LaunchWaitingFor => FindApp(_runningAppId) is not { } app ? null : app switch
    {
        { Type: "steam" } => "Steam",
        { Source: "Battle.net" or "Riot Games" or "Epic Games" } => app.Source,
        _ => app.Name,
    };

    public bool CanLeaveRunningApp =>
        _runningAppId != null && !_isWaitingForLaunch && FindApp(_runningAppId) is { Type: not "web" };

    public void ShowMessage(string message) => Message = message;

    // Runs async work from a command or an event. A lost connection is expected and is reported
    // through the connection status, so it isn't an error here.
    private async void Guard(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (CouchException e)
        {
            AppLog.Write($"Tachyon request failed: {e.Message}");
        }
    }

    private async Task OnConnectionChangedAsync(bool connected)
    {
        IsConnected = connected;
        if (!connected) return;

        await PublishCatalogAsync();
        await LoadStateAsync();
        RebuildRows();
        await SyncRunningAppAsync();
        if (_focusPending && _runningApp == null)
        {
            _focusPending = false;
            FocusRequested?.Invoke(_lastTile);
        }
    }

    // apps.json is the source; Tachyon is what every client reads.
    private async Task PublishCatalogAsync()
    {
        RespValue previous = await _client.SendAsync("LRANGE", AppsKey, "0", "-1");
        foreach (RespValue id in previous.ItemsOrEmpty)
        {
            await _client.SendAsync("DEL", "app:" + id.Text);
        }
        await _client.SendAsync("DEL", AppsKey);
        foreach (AppDefinition app in _catalog)
        {
            await _client.SendAsync(["HSET", "app:" + app.Id, .. app.ToHashFields()]);
            await _client.SendAsync("RPUSH", AppsKey, app.Id);
        }
    }

    private async Task LoadStateAsync()
    {
        var apps = new List<AppDefinition>();
        foreach (RespValue id in (await _client.SendAsync("LRANGE", AppsKey, "0", "-1")).ItemsOrEmpty)
        {
            AppDefinition? app = AppDefinition.FromHash((await _client.SendAsync("HGETALL", "app:" + id.Text)).ToDictionary());
            if (app != null) apps.Add(app);
        }
        _apps = apps;

        Dictionary<string, string> settings = (await _client.SendAsync("HGETALL", SettingsKey)).ToDictionary();
        _browser = settings.GetValueOrDefault("browser", "chrome");
        string pack = settings.GetValueOrDefault("sounds", DefaultSoundPack);
        SoundPack = SoundPacks.Any(known => known.Id == pack) ? pack : DefaultSoundPack;
        if (int.TryParse(settings.GetValueOrDefault("pointerSpeed"), NumberStyles.None, CultureInfo.InvariantCulture, out int speed))
        {
            PointerSpeed = Math.Clamp(speed, PointerStep, HighPointer);
        }
        _lastTile = settings.GetValueOrDefault("lastTile");

        Dictionary<string, string> volume = (await _client.SendAsync("HGETALL", VolumeKey)).ToDictionary();
        if (int.TryParse(volume.GetValueOrDefault("percent"), NumberStyles.None, CultureInfo.InvariantCulture, out int percent))
        {
            VolumePercent = percent;
            IsMuted = volume.GetValueOrDefault("muted") == "1";
            _volumeKnown = true;
        }

        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        _recent = (await _client.SendAsync("LRANGE", RecentKey, "0", "-1")).ItemsOrEmpty.Select(v => v.Text ?? "").ToList();
        _sessions = (await _client.SendAsync("LRANGE", SessionsKey, "0", (HistoryTiles - 1).ToString(CultureInfo.InvariantCulture)))
            .ItemsOrEmpty.Select(v => v.Text ?? "").ToList();
    }

    // After a connect or a UI restart: an app may have started or exited while we weren't looking.
    private async Task SyncRunningAppAsync()
    {
        string? running = (await _client.SendAsync("GET", RunningKey)).Text;
        bool alive = (await _client.SendAsync("EXISTS", AliveKey)).Integer > 0;

        if (running != null && alive)
        {
            _runningApp = running;
            _missedChecks = 0;
            if (!_window.IsHidden) _window.Hide();
        }
        else if (_window.IsHidden)
        {
            string? finished = _runningApp;
            _isWaitingForLaunch = false;
            _runningApp = null;
            await ComeBackAsync(finished);
        }
        else
        {
            _runningApp = null;
        }
    }

    // Called every couple of seconds by the view while an app runs, as a safety net for a missed
    // event or a dead helper.
    public async Task CheckRunningAppAsync()
    {
        if (_runningApp == null) return;
        try
        {
            string? running = (await _client.SendAsync("GET", RunningKey)).Text;
            if (running == null && !_isWaitingForLaunch)
            {
                await FinishAppAsync(_runningApp);
                return;
            }
            bool alive = (await _client.SendAsync("EXISTS", AliveKey)).Integer > 0;
            _missedChecks = alive ? 0 : _missedChecks + 1;
        }
        catch (CouchException)
        {
            _missedChecks++;
        }

        if (_missedChecks >= MaxMissedChecks)
        {
            string finished = _runningApp;
            _isWaitingForLaunch = false;
            _runningApp = null;
            _missedChecks = 0;
            _window.RestoreAndFocus();
            FocusRequested?.Invoke(finished);
            Message = "Lost track of the running app. CouchShell's background service stopped responding.";
        }
    }

    private async Task OnEventAsync(string payload)
    {
        string[] parts = payload.Split(' ', 3);
        string kind = parts[0];
        string argument = parts.Length > 1 ? parts[1] : "";

        switch (kind)
        {
            case "launching":
                // Get out of the way, so that whatever Steam is showing can be used.
                _isWaitingForLaunch = true;
                _missedChecks = 0;
                Message = null;
                _window.Hide();
                _runningApp = argument;
                break;
            case "started":
                _isWaitingForLaunch = false;
                _missedChecks = 0;
                Message = null;
                _window.Hide();
                _runningApp = argument;
                break;
            case "launch_cancelled":
                _isWaitingForLaunch = false;
                await FinishAppAsync(argument);
                break;
            case "exited":
                await FinishAppAsync(argument);
                break;
            case "launch_failed":
                bool wasWaiting = _isWaitingForLaunch && argument == _runningApp;
                if (wasWaiting)
                {
                    _isWaitingForLaunch = false;
                    await FinishAppAsync(argument);
                }
                string reason = parts.Length > 2 ? parts[2] : "";
                Message = $"Unable to launch {AppName(argument)}. {reason}";
                break;
            case "volume" when parts.Length > 2:
                OnVolumeChanged(argument, parts[2]);
                break;
            case "power_failed":
                Message = "That didn't work. " + payload["power_failed".Length..].Trim();
                break;
        }
    }

    private async Task FinishAppAsync(string id)
    {
        _isWaitingForLaunch = false;
        _runningApp = null;
        await ComeBackAsync(id);
    }

    // Shows the window again, on the tile that launched.
    private async Task ComeBackAsync(string? id)
    {
        Message = null;
        if (_window.IsHidden) _window.RestoreAndFocus();
        try
        {
            await LoadHistoryAsync();
        }
        finally
        {
            RebuildRows();
            FocusRequested?.Invoke(id);
        }
    }

    private void OnVolumeChanged(string percentText, string mutedText)
    {
        if (!int.TryParse(percentText, NumberStyles.None, CultureInfo.InvariantCulture, out int percent)) return;
        bool muted = mutedText == "1";
        bool changed = _volumeKnown && (percent != VolumePercent || muted != IsMuted);
        VolumePercent = percent;
        IsMuted = muted;
        _volumeKnown = true;
        UpdateGuideValues();
        if (changed && !IsGuideOpen) ShowVolumeOverlay();  // the menu shows the volume itself
    }

    private async void ShowVolumeOverlay()
    {
        IsVolumeVisible = true;
        int version = ++_volumeOverlayVersion;
        await Task.Delay(VolumeOverlayTime);
        // Another change restarted the timer if the version moved on.
        if (version == _volumeOverlayVersion) IsVolumeVisible = false;
    }

    // Selecting a tab: show its screen and move into it.
    private void OpenScreen(string name)
    {
        ShowScreen(name);
        Message = null;
        FocusRequested?.Invoke(name == SearchScreen ? FirstSearchKey : null);
    }

    // Moving onto a tab: show what's in its screen, with focus staying on the tab.
    public void ShowScreen(string name)
    {
        if (name == CurrentScreen) return;
        _searchText = "";
        CurrentScreen = name;
        foreach (TabViewModel tab in Tabs) tab.IsCurrent = tab.Name == name;
        RebuildRows();
        FocusedTile = IsSearchScreen ? SearchPreview() : Rows.FirstOrDefault()?.Tiles.FirstOrDefault();
    }

    private void Back()
    {
        if (IsConfirmOpen)
        {
            CloseConfirm();
            return;
        }
        if (IsGuideOpen)
        {
            CloseGuide();
            return;
        }
        Message = null;
        // While searching, B works like backspace, as it does on the on-screen keyboard.
        if (IsSearchScreen && _searchText.Length > 0)
        {
            DeleteSearch();
            return;
        }
        if (CurrentScreen != HomeScreen) OpenScreen(HomeScreen);
    }

    private void RebuildRows()
    {
        Rows.Clear();
        switch (CurrentScreen)
        {
            case HomeScreen:
                AddRecentRow();
                AddAppRow("All apps", _apps);
                break;
            case GamesScreen:
                AddGamesRows();
                break;
            case WatchScreen:
                AddAppRow("Watch", _apps.Where(app => app.Category == "watch"));
                break;
            case AppsScreen:
                AddAppRow("Apps", _apps.Where(app => app.Category == "apps"));
                break;
            case SearchScreen:
                AddAppRow(_searchText.Length == 0 ? "All apps" : "Results",
                    InstalledApps.Search(_installedApps, _searchText).Take(SearchResults));
                break;
            case HistoryScreen:
                AddHistoryRow();
                break;
            case SettingsScreen:
                AddSettingsRows();
                break;
        }
        if (Rows.Count == 0) FocusedTile = null;
        if (IsSectionScreen) SelectedSection = Rows.Any(row => row.Title == SelectedSection) ? SelectedSection : Rows.FirstOrDefault()?.Title;
        UpdateEmptyText();
    }

    // Every game, then one section per store, so a big library can be narrowed down.
    private void AddGamesRows()
    {
        List<AppDefinition> games = _apps.Where(app => app.Category == "games").ToList();
        AddAppRow("All games", games);
        var stores = games.GroupBy(game => game.Source ?? (game.Type == "steam" ? "Steam" : "Other")).OrderBy(store => store.Key).ToList();
        if (stores.Count < 2) return;  // the one store's list would just repeat All games
        foreach (IGrouping<string, AppDefinition> store in stores) AddAppRow(store.Key, store);
    }

    private void UpdateEmptyText()
    {
        if (Rows.Count > 0)
        {
            EmptyText = null;
        }
        else if (!IsConnected)
        {
            EmptyText = "Connecting...";
        }
        else
        {
            EmptyText = CurrentScreen switch
            {
                HistoryScreen => "Nothing played yet.",
                SearchScreen => _installedApps.Count == 0 ? "Finding your apps..." : "No apps match that.",
                _ => "No apps here yet. Add some to apps.json.",
            };
        }
    }

    private void AddRecentRow()
    {
        var recent = _recent.Distinct()
            .Select(id => FindApp(id))
            .OfType<AppDefinition>()
            .Take(RecentTiles);
        AddAppRow("Recently used", recent);
        // The latest one leads, wider, with its full artwork when it has some.
        if (Rows.Count > 0 && Rows[0].Title == "Recently used")
        {
            List<TileViewModel> tiles = [.. Rows[0].Tiles];
            AppDefinition? first = FindApp(tiles[0].Key);
            tiles[0] = tiles[0] with { IsWide = true, Image = first?.Backdrop ?? tiles[0].Image };
            Rows[0] = Rows[0] with { Tiles = tiles };
        }
    }

    private void AddAppRow(string title, IEnumerable<AppDefinition> apps)
    {
        List<TileViewModel> tiles = apps
            .Select(app => TileFor(app, new RelayCommand(_ => Guard(() => LaunchAsync(app)))))
            .ToList();
        if (tiles.Count > 0) Rows.Add(new TileRow(title, tiles));
    }

    private TileViewModel TileFor(AppDefinition app, ICommand command)
    {
        BrandLogo? logo = BrandLogos.Find(app.Logo);
        return new TileViewModel(app.Id, app.Name, null, app.Image, command,
            Accent: app.Color ?? logo?.Accent ?? BrandLogos.DefaultAccent,
            LogoPath: app.Image == null ? logo?.Path : null,
            LogoColor: logo?.Color,
            Backdrop: app.Backdrop,
            Description: WithLastPlayed(DescribeApp(app), app.Id),
            TitleImage: app.TitleImage,
            ActionLabel: app.Category == "games" ? "Play" : "Open",
            IconPath: app.Image == null && logo == null ? app.Icon : null);
    }

    private static string DescribeApp(AppDefinition app) => app.Source ?? app.Type switch
    {
        "steam" => "Steam game",
        "web" => app.Window == "browser" ? "Web browser" : "Web app",
        _ => "App",
    };

    // "Steam  ·  Last played yesterday", from the play history.
    private string WithLastPlayed(string description, string id)
    {
        foreach (string session in _sessions)
        {
            string[] parts = session.Split('\t');
            if (parts.Length == 3 && parts[0] == id && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long end))
                return $"{description}  ·  Last played {DescribeDay(end, DateTimeOffset.Now)}";
        }
        return description;
    }

    public static string DescribeDay(long endMs, DateTimeOffset now)
    {
        DateTime day = DateTimeOffset.FromUnixTimeMilliseconds(endMs).ToOffset(now.Offset).Date;
        int daysAgo = (now.Date - day).Days;
        return daysAgo switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 7 => $"{daysAgo} days ago",
            _ => day.ToString("MMM d", CultureInfo.CurrentCulture),
        };
    }

    private void AddHistoryRow()
    {
        var doNothing = new RelayCommand(_ => { });
        var tiles = new List<TileViewModel>();
        for (int i = 0; i < _sessions.Count; i++)
        {
            string[] parts = _sessions[i].Split('\t');
            if (parts.Length != 3 ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long start) ||
                !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long end))
            {
                continue;
            }
            AppDefinition? app = FindApp(parts[0]);
            TileViewModel tile = app != null
                ? TileFor(app, doNothing)
                : new TileViewModel("", parts[0], null, null, doNothing, Glyphs.History);
            string when = DescribeSession(start, end);
            tiles.Add(tile with { Key = "session:" + i, Subtitle = when, Description = when });
        }
        if (tiles.Count > 0) Rows.Add(new TileRow("Play history", tiles));
    }

    public static string DescribeSession(long startMs, long endMs)
    {
        DateTimeOffset started = DateTimeOffset.FromUnixTimeMilliseconds(startMs).ToLocalTime();
        long minutes = Math.Max(0, endMs - startMs) / 60000;
        string length = minutes < 1 ? "under a minute" : minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
        return $"{started.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture)} · {length}";
    }

    private void AddSettingsRows()
    {
        Rows.Add(new TileRow("Sound",
        [
            new TileViewModel("sound:down", "Volume down", null, null, VolumeDownCommand, Glyphs.VolumeDown, Description: "Also LB on the controller"),
            new TileViewModel("sound:up", "Volume up", null, null, VolumeUpCommand, Glyphs.VolumeUp, Description: "Also RB on the controller"),
            new TileViewModel("sound:mute", "Mute", null, null, MuteCommand, Glyphs.Mute, Description: "Also Y on the controller"),
            new TileViewModel("sound:ui", "Interface sounds", SoundPacks.First(known => known.Id == SoundPack).Label, null,
                new RelayCommand(_ => Guard(NextSoundPackAsync)), Glyphs.Sounds, Description: "Press A to try the next style, then move to hear it"),
        ]));
        Rows.Add(new TileRow("Controller",
        [
            PointerTile("Slow", SlowPointer),
            PointerTile("Medium", MediumPointer),
            PointerTile("High", HighPointer),
            new TileViewModel("pointer:slower", "Pointer slower", $"Now {PointerSpeed}%", null,
                new RelayCommand(_ => Guard(() => SetPointerSpeedAsync(PointerSpeed - PointerStep, "pointer:slower"))), Glyphs.Slower,
                Description: $"How fast the left stick moves the pointer in apps, in steps of {PointerStep}%"),
            new TileViewModel("pointer:faster", "Pointer faster", $"Now {PointerSpeed}%", null,
                new RelayCommand(_ => Guard(() => SetPointerSpeedAsync(PointerSpeed + PointerStep, "pointer:faster"))), Glyphs.Faster,
                Description: $"How fast the left stick moves the pointer in apps, in steps of {PointerStep}%"),
        ]));
        Rows.Add(new TileRow("Web apps",
        [
            BrowserTile("chrome", "Google Chrome"),
            BrowserTile("edge", "Microsoft Edge"),
        ]));
        Rows.Add(new TileRow("Power",
        [
            ConfirmTile("exit", "Exit CouchShell", "Exit CouchShell and go back to Windows?", Glyphs.Exit, () =>
            {
                ExitRequested?.Invoke();
                return Task.CompletedTask;
            }),
            PowerTile("sleep", "Sleep", "Put the PC to sleep?", Glyphs.Sleep),
            PowerTile("restart", "Restart", "Restart the PC?", Glyphs.Restart),
            PowerTile("shutdown", "Shut down", "Shut down the PC?", Glyphs.Power),
        ]));
    }

    private TileViewModel BrowserTile(string id, string name) =>
        new("browser:" + id, name, _browser == id ? "Selected" : null, null,
            new RelayCommand(_ => Guard(() => SetBrowserAsync(id))), Glyphs.Globe,
            Description: "The browser that web apps open in");

    private TileViewModel PowerTile(string action, string name, string question, string glyph) =>
        ConfirmTile(action, name, question, glyph, () => RequestAsync("power " + action));

    private TileViewModel ConfirmTile(string action, string name, string question, string glyph, Func<Task> confirmed)
    {
        string key = "power:" + action;
        return new TileViewModel(key, name, null, null, new RelayCommand(_ => AskToConfirm(question, key, confirmed)),
            glyph, Description: "Asks you to confirm first");
    }

    private void AskToConfirm(string question, string returnKey, Func<Task> confirmed)
    {
        _confirmAction = confirmed;
        _confirmReturnKey = returnKey;
        ConfirmText = question;
        FocusRequested?.Invoke("confirm:no");  // the safe answer starts focused
    }

    private async Task ConfirmYesAsync()
    {
        Func<Task>? action = _confirmAction;
        if (IsGuideOpen)
        {
            // Done with the menu too: focus goes back to where it was before it opened.
            IsGuideOpen = false;
            _confirmReturnKey = _guideReturnKey;
        }
        CloseConfirm();
        if (action != null) await action();
    }

    private void CloseConfirm()
    {
        _confirmAction = null;
        ConfirmText = null;
        FocusRequested?.Invoke(_confirmReturnKey);
    }

    private TileViewModel PointerTile(string name, int percent) =>
        new("pointer:" + percent, "Pointer speed: " + name, PointerSpeed == percent ? "Selected" : $"{percent}%", null,
            new RelayCommand(_ => Guard(() => SetPointerSpeedAsync(percent, "pointer:" + percent))), Glyphs.Pointer,
            Description: "How fast the left stick moves the pointer in apps");

    private async Task SetPointerSpeedAsync(int percent, string focusKey)
    {
        int speed = Math.Clamp(percent, PointerStep, HighPointer);
        await _client.SendAsync("HSET", SettingsKey, "pointerSpeed", speed.ToString(CultureInfo.InvariantCulture));
        PointerSpeed = speed;
        UpdateGuideValues();
        RebuildRows();
        FocusRequested?.Invoke(focusKey);
    }

    private async Task NextSoundPackAsync()
    {
        int current = Array.FindIndex(SoundPacks, known => known.Id == SoundPack);
        string next = SoundPacks[(current + 1) % SoundPacks.Length].Id;
        await _client.SendAsync("HSET", SettingsKey, "sounds", next);
        SoundPack = next;
        UpdateGuideValues();
        RebuildRows();
        if (!IsGuideOpen) FocusRequested?.Invoke("sound:ui");
    }

    private async Task SetBrowserAsync(string id)
    {
        await _client.SendAsync("HSET", SettingsKey, "browser", id);
        _browser = id;
        RebuildRows();
        FocusRequested?.Invoke("browser:" + id);
    }

    private List<GuideItem> BuildGuide()
    {
        var doNothing = new RelayCommand(_ => { });
        return
        [
            new("guide:volume", "Volume", Glyphs.VolumeUp, doNothing, isSlider: true, group: "Quick settings"),
            new("guide:mute", "Mute", Glyphs.Mute, MuteCommand),
            new("guide:sounds", "Interface sounds", Glyphs.Sounds, new RelayCommand(_ => Guard(NextSoundPackAsync))),
            new("guide:pointer", "Pointer speed", Glyphs.Pointer, new RelayCommand(_ => Guard(NextPointerSpeedAsync))),
            GuideConfirm("exit", "Exit to Windows", "Exit CouchShell and go back to Windows?", Glyphs.Exit, () =>
            {
                ExitRequested?.Invoke();
                return Task.CompletedTask;
            }, "Power"),
            GuideConfirm("sleep", "Sleep", "Put the PC to sleep?", Glyphs.Sleep, () => RequestAsync("power sleep")),
            GuideConfirm("restart", "Restart", "Restart the PC?", Glyphs.Restart, () => RequestAsync("power restart")),
            GuideConfirm("shutdown", "Shut down", "Shut down the PC?", Glyphs.Power, () => RequestAsync("power shutdown")),
        ];
    }

    private GuideItem GuideConfirm(string action, string label, string question, string glyph, Func<Task> confirmed, string? group = null)
    {
        string key = "guide:" + action;
        return new GuideItem(key, label, glyph, new RelayCommand(_ => AskToConfirm(question, key, confirmed)), group: group);
    }

    private void UpdateGuideValues()
    {
        foreach (GuideItem item in GuideItems)
        {
            item.Value = item.Key switch
            {
                "guide:volume" => IsMuted ? "Muted" : VolumePercent.ToString(CultureInfo.CurrentCulture),
                "guide:mute" => IsMuted ? "On" : "Off",
                "guide:sounds" => SoundPacks.First(known => known.Id == SoundPack).Label,
                "guide:pointer" => PointerSpeed switch { SlowPointer => "Slow", MediumPointer => "Medium", HighPointer => "High", _ => $"{PointerSpeed}%" },
                _ => null,
            };
        }
    }

    private void ToggleGuide()
    {
        if (IsGuideOpen) CloseGuide();
        else OpenGuide();
    }

    public void OpenGuide()
    {
        if (IsGuideOpen || IsConfirmOpen) return;
        _guideReturnKey = FocusedTile?.Key;
        UpdateGuideValues();
        IsGuideOpen = true;
        FocusRequested?.Invoke("guide:volume");
    }

    private void CloseGuide()
    {
        IsGuideOpen = false;
        FocusRequested?.Invoke(_guideReturnKey);
    }

    // Slow, Medium, High, then back to Slow. A finer setting from Settings goes to Slow next.
    private Task NextPointerSpeedAsync()
    {
        int next = PointerSpeed switch { SlowPointer => MediumPointer, MediumPointer => HighPointer, _ => SlowPointer };
        return SetPointerSpeedAsync(next, "guide:pointer");
    }

    private async Task LaunchAsync(AppDefinition app)
    {
        Message = $"Starting {app.Name}...";
        _lastTile = app.Id;
        try
        {
            await _client.SendAsync("HSET", SettingsKey, "lastTile", app.Id);
            // Apps found by search aren't in the catalog, so couchd hears about them here.
            if (app.Type == "shell") await _client.SendAsync(["HSET", "app:" + app.Id, .. app.ToHashFields()]);
            long listeners = (await _client.SendAsync("PUBLISH", RequestChannel, "launch " + app.Id)).Integer;
            if (listeners == 0) Message = $"Unable to launch {app.Name}. CouchShell's background service isn't running.";
        }
        catch (CouchException)
        {
            Message = $"Unable to launch {app.Name}. Not connected to Tachyon.";
        }
    }

    // Sends a request to couchd, and says so if nothing is listening.
    private async Task RequestAsync(string request)
    {
        try
        {
            long listeners = (await _client.SendAsync("PUBLISH", RequestChannel, request)).Integer;
            if (listeners == 0) Message = "That didn't work. CouchShell's background service isn't running.";
        }
        catch (CouchException)
        {
            Message = "That didn't work. Not connected to Tachyon.";
        }
    }

    private string AppName(string id) => FindApp(id)?.Name ?? id;
}
