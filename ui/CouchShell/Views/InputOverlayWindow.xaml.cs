using System.Windows;
using System.Windows.Interop;
using CouchShell.Platform;

namespace CouchShell.Views;

public partial class InputOverlayWindow : Window
{
    public InputOverlayWindow()
    {
        InitializeComponent();

        // Covers the whole primary screen, in WPF's device-independent units.
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        SourceInitialized += (_, _) => WindowsNative.MakeOverlay(new WindowInteropHelper(this).Handle);
    }
}
