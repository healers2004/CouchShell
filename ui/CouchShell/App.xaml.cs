using System.Threading.Tasks;
using System.Windows;
using CouchShell.Services;
using CouchShell.ViewModels;
using CouchShell.Views;

namespace CouchShell;

// Application entry point: builds the pieces and connects them.
public partial class App : Application
{
    private const string TachyonHost = "127.0.0.1";
    private const int TachyonPort = 6380;

    private const int BackendPatienceSeconds = 15;

    private CouchClient? _client;
    private MainViewModel? _viewModel;
    private Mutex? _singleInstance;  // held for as long as this copy runs
    private bool _isOnlyCopy;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstallErrorHandlers();

        // Windows releases a named mutex when its owner ends, even after a crash.
        _singleInstance = new Mutex(true, @"Local\CouchShell", out _isOnlyCopy);
        if (!_isOnlyCopy)
        {
            ShowTheRunningCopy();
            Shutdown();
            return;
        }

        if (!SystemParameters.ClientAreaAnimation) Resources["MotionDuration"] = new Duration(TimeSpan.Zero);

        bool backendStarted = BackendHost.Start(AppLog.Write);

        List<AppDefinition> catalog = AppCatalog.LoadAll(AppLog.Write);

        _client = new CouchClient(TachyonHost, TachyonPort, [MainViewModel.EventChannel],
            action => Dispatcher.InvokeAsync(action));

        var window = new MainWindow();
        var windowService = new WindowService(window);
        _viewModel = new MainViewModel(_client, catalog, windowService);
        _viewModel.ExitRequested += () => Shutdown();
        window.DataContext = _viewModel;
        MainWindow = window;
        window.Show();
        // Started at logon it isn't the active window, and input only reaches the active window.
        windowService.RestoreAndFocus();
        if (e.Args.Contains("--at-logon")) KeepInFrontWhileWindowsStarts(window, windowService);

        _client.Start();
        ReportIfBackendStaysAway(backendStarted);

        // COM needs an STA thread, and reading every app shouldn't hold up the UI.
        var appsThread = new Thread(() =>
        {
            List<AppDefinition> apps = InstalledApps.Load(AppLog.Write);
            Dispatcher.InvokeAsync(() => _viewModel?.SetInstalledApps(apps));
        }) { IsBackground = true };
        appsThread.SetApartmentState(ApartmentState.STA);
        appsThread.Start();
    }

    // At logon, other programs (Steam, Spotify...) keep opening their windows for a while, and input
    // only reaches the window in front.
    private void KeepInFrontWhileWindowsStarts(Window window, IWindowService windowService)
    {
        var stopAt = DateTime.Now.AddSeconds(45);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (DateTime.Now > stopAt) timer.Stop();
            else if (!windowService.IsHidden && !window.IsActive) windowService.RestoreAndFocus();
        };
        timer.Start();
    }

    // Opened a second time, from a shortcut say. While an app is open this window is hidden and has
    // no handle to find, which is right: the app stays in front.
    private static void ShowTheRunningCopy()
    {
        int ours = Environment.ProcessId;
        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcessesByName("CouchShell"))
        {
            using (process)
            {
                if (process.Id != ours && process.MainWindowHandle != IntPtr.Zero)
                    Platform.WindowsNative.ForceForeground(process.MainWindowHandle);
            }
        }
    }

    // Without this, a backend that can't start (say, the port is taken) just reads "Connecting..." forever.
    private void ReportIfBackendStaysAway(bool started)
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = started ? TimeSpan.FromSeconds(BackendPatienceSeconds) : TimeSpan.FromMilliseconds(1),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_viewModel is { IsConnected: false })
                _viewModel.ShowMessage($"CouchShell's background service isn't responding. Details are in {AppLog.Folder}");
        };
        timer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _client?.Dispose();
        if (_isOnlyCopy) BackendHost.Stop(AppLog.Write);
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    // Unexpected errors are logged and shown as a short message; raw details only in debug builds.
    private void InstallErrorHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write($"Unhandled UI error: {args.Exception}");
            if (_viewModel == null || MainWindow is not { IsLoaded: true })
            {
                // Failed during startup: don't leave a process running with no window.
                Shutdown(1);
                args.Handled = true;
                return;
            }
#if DEBUG
            _viewModel?.ShowMessage(args.Exception.Message);
#else
            _viewModel?.ShowMessage("Something went wrong, but CouchShell is still running.");
#endif
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write($"Unobserved task error: {args.Exception}");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Write($"Fatal error: {args.ExceptionObject}");
    }
}
