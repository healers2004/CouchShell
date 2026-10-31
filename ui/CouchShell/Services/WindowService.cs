using System.Windows;
using System.Windows.Interop;
using CouchShell.Platform;

namespace CouchShell.Services;

public interface IWindowService
{
    bool IsHidden { get; }
    void Hide();
    void RestoreAndFocus();
}

// Owns hiding the window and taking focus back. The Win32 calls stay in WindowsNative.
public sealed class WindowService(Window window) : IWindowService
{
    public bool IsHidden => !window.IsVisible;

    public void Hide() => window.Hide();

    public void RestoreAndFocus()
    {
        window.Show();
        window.WindowState = WindowState.Maximized;
        // Windows often refuses to let a background program take focus, so ask harder.
        bool focused = WindowsNative.ForceForeground(new WindowInteropHelper(window).Handle);
        window.Activate();
        if (!focused) AppLog.Write("Windows refused the focus change; the next controller press retries.");
    }
}
