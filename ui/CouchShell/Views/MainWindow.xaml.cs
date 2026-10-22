using System.Windows;
using System.Windows.Input;

namespace CouchShell.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Put keyboard focus on the first tab at startup, so arrow keys work without pressing Tab first.
        Loaded += (_, _) => MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
}
