using System.Windows.Input;

namespace CouchShell.ViewModels;

// One selectable tile. Key identifies it for focus: an app's id, or e.g. "power:sleep".
public sealed record TileViewModel(string Key, string Title, string? Subtitle, string? Image, ICommand Command);

public sealed record TileRow(string Title, IReadOnlyList<TileViewModel> Tiles);

public sealed class TabViewModel(string name, ICommand command) : ViewModelBase
{
    private bool _isCurrent;

    public string Name { get; } = name;
    public string FocusKey => "tab:" + Name;
    public ICommand Command { get; } = command;

    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }
}
