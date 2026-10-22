using System.ComponentModel;
using System.Windows.Input;

namespace CouchShell.ViewModels;

// State behind the main window: the list of screens and which one is showing.
// Knows nothing about WPF controls; the view binds to these properties and commands.
public class MainViewModel : INotifyPropertyChanged
{
    public const string HomeScreen = "Home";

    public string[] Screens { get; } = [HomeScreen, "Games", "Watch", "Apps", "Settings"];

    private string _currentScreen = HomeScreen;

    public string CurrentScreen
    {
        get => _currentScreen;
        private set
        {
            _currentScreen = value;
            // Tells bound controls to re-read CurrentScreen.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentScreen)));
        }
    }

    // Enter on a tab: the tab passes its screen name as the parameter.
    public ICommand OpenScreenCommand { get; }

    // Escape: back to Home.
    public ICommand GoHomeCommand { get; }

    public MainViewModel()
    {
        OpenScreenCommand = new RelayCommand(screen => CurrentScreen = (string)screen!);
        GoHomeCommand = new RelayCommand(_ => CurrentScreen = HomeScreen);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
