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
    private const string HistoryScreen = "History";
    private const string SettingsScreen = "Settings";

    private const int RecentTiles = 6;
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
    private string _browser = "edge";
    private string? _lastTile;
    private string? _runningApp;
    private int _missedChecks;
    private bool _focusPending = true;

    private string _currentScreen = HomeScreen;
    private bool _isConnected;
    private string? _message;
    private int _volumePercent;
    private bool _isMuted;
    private bool _volumeKnown;
    private bool _isVolumeVisible;
    private int _volumeOverlayVersion;
    private string? _confirmText;
    private Func<Task>? _confirmAction;
    private string? _confirmReturnKey;

    public MainViewModel(ICouchClient client, IReadOnlyList<AppDefinition> catalog, IWindowService window)
    {
        _client = client;
        _catalog = catalog;
        _window = window;

        string[] screens = [HomeScreen, GamesScreen, WatchScreen, AppsScreen, HistoryScreen, SettingsScreen];
        Tabs = screens.Select(name => new TabViewModel(name, new RelayCommand(_ => OpenScreen(name)))).ToList();
        Tabs[0].IsCurrent = true;

        BackCommand = new RelayCommand(_ => Back());
        ConfirmYesCommand = new RelayCommand(_ => Guard(ConfirmYesAsync));
        ConfirmNoCommand = new RelayCommand(_ => CloseConfirm());
        VolumeDownCommand = new RelayCommand(_ => Guard(() => RequestAsync("volume down")));
        VolumeUpCommand = new RelayCommand(_ => Guard(() => RequestAsync("volume up")));
        MuteCommand = new RelayCommand(_ => Guard(() => RequestAsync("volume mute")));

        client.ConnectionChanged += connected => Guard(() => OnConnectionChangedAsync(connected));
        client.MessageReceived += (channel, payload) =>
        {
            if (channel == EventChannel) Guard(() => OnEventAsync(payload));
        };
    }

    public IReadOnlyList<TabViewModel> Tabs { get; }
    public ObservableCollection<TileRow> Rows { get; } = [];

    public ICommand BackCommand { get; }
    public ICommand ConfirmYesCommand { get; }
    public ICommand ConfirmNoCommand { get; }
    public ICommand VolumeDownCommand { get; }
    public ICommand VolumeUpCommand { get; }
    public ICommand MuteCommand { get; }

    // Asks the view to focus a tile by key. Null means the first tile on the screen.
    public event Action<string?>? FocusRequested;

    public string CurrentScreen
    {
        get => _currentScreen;
        private set => Set(ref _currentScreen, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => Set(ref _isConnected, value);
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
        private set => Set(ref _isConfirmOpen, value);
    }

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
        _browser = settings.GetValueOrDefault("browser", "edge");
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
            if (running == null)
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
            case "started":
                _runningApp = argument;
                _missedChecks = 0;
                Message = null;
                _window.Hide();
                break;
            case "exited":
                await FinishAppAsync(argument);
                break;
            case "launch_failed":
                _runningApp = null;
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
        if (changed) ShowVolumeOverlay();
    }

    private async void ShowVolumeOverlay()
    {
        IsVolumeVisible = true;
        int version = ++_volumeOverlayVersion;
        await Task.Delay(VolumeOverlayTime);
        // Another change restarted the timer if the version moved on.
        if (version == _volumeOverlayVersion) IsVolumeVisible = false;
    }

    private void OpenScreen(string name)
    {
        if (name != CurrentScreen)
        {
            CurrentScreen = name;
            foreach (TabViewModel tab in Tabs) tab.IsCurrent = tab.Name == name;
            RebuildRows();
        }
        Message = null;
        FocusRequested?.Invoke(null);
    }

    private void Back()
    {
        if (IsConfirmOpen)
        {
            CloseConfirm();
            return;
        }
        Message = null;
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
                AddAppRow("Games", _apps.Where(app => app.Category == "games"));
                break;
            case WatchScreen:
                AddAppRow("Watch", _apps.Where(app => app.Category == "watch"));
                break;
            case AppsScreen:
                AddAppRow("Apps", _apps.Where(app => app.Category == "apps"));
                break;
            case HistoryScreen:
                AddHistoryRow();
                break;
            case SettingsScreen:
                AddSettingsRows();
                break;
        }
    }

    private void AddRecentRow()
    {
        var recent = _recent.Distinct()
            .Select(id => _apps.Find(app => app.Id == id))
            .OfType<AppDefinition>()
            .Take(RecentTiles);
        AddAppRow("Recently used", recent);
    }

    private void AddAppRow(string title, IEnumerable<AppDefinition> apps)
    {
        List<TileViewModel> tiles = apps
            .Select(app => new TileViewModel(app.Id, app.Name, null, app.Image,
                new RelayCommand(_ => Guard(() => LaunchAsync(app)))))
            .ToList();
        if (tiles.Count > 0) Rows.Add(new TileRow(title, tiles));
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
            tiles.Add(new TileViewModel("session:" + i, AppName(parts[0]), DescribeSession(start, end), null, doNothing));
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
        Rows.Add(new TileRow("Browser for web apps",
        [
            BrowserTile("edge", "Microsoft Edge"),
            BrowserTile("chrome", "Google Chrome"),
        ]));
        Rows.Add(new TileRow("Sound",
        [
            new TileViewModel("sound:down", "Volume down", "LB", null, VolumeDownCommand),
            new TileViewModel("sound:up", "Volume up", "RB", null, VolumeUpCommand),
            new TileViewModel("sound:mute", "Mute", "Y", null, MuteCommand),
        ]));
        Rows.Add(new TileRow("Power",
        [
            PowerTile("sleep", "Sleep", "Put the PC to sleep?"),
            PowerTile("restart", "Restart", "Restart the PC?"),
            PowerTile("shutdown", "Shut down", "Shut down the PC?"),
        ]));
    }

    private TileViewModel BrowserTile(string id, string name) =>
        new("browser:" + id, name, _browser == id ? "Selected" : null, null,
            new RelayCommand(_ => Guard(() => SetBrowserAsync(id))));

    private TileViewModel PowerTile(string action, string name, string question)
    {
        string key = "power:" + action;
        return new TileViewModel(key, name, null, null, new RelayCommand(_ =>
        {
            _confirmAction = () => RequestAsync("power " + action);
            _confirmReturnKey = key;
            ConfirmText = question;
            FocusRequested?.Invoke("confirm:no");  // the safe answer starts focused
        }));
    }

    private async Task ConfirmYesAsync()
    {
        Func<Task>? action = _confirmAction;
        CloseConfirm();
        if (action != null) await action();
    }

    private void CloseConfirm()
    {
        _confirmAction = null;
        ConfirmText = null;
        FocusRequested?.Invoke(_confirmReturnKey);
    }

    private async Task SetBrowserAsync(string id)
    {
        await _client.SendAsync("HSET", SettingsKey, "browser", id);
        _browser = id;
        RebuildRows();
        FocusRequested?.Invoke("browser:" + id);
    }

    private async Task LaunchAsync(AppDefinition app)
    {
        Message = $"Starting {app.Name}...";
        _lastTile = app.Id;
        try
        {
            await _client.SendAsync("HSET", SettingsKey, "lastTile", app.Id);
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

    private string AppName(string id) => _apps.Find(app => app.Id == id)?.Name ?? id;
}
