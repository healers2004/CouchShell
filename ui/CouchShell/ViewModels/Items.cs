using System.Windows.Input;

namespace CouchShell.ViewModels;

// Glyphs from the Segoe MDL2 Assets font, which ships with Windows.
public static class Glyphs
{
    public const string History = "";
    public const string Globe = "";
    public const string VolumeDown = "";
    public const string VolumeUp = "";
    public const string Mute = "";
    public const string Sleep = "";
    public const string Restart = "";
    public const string Power = "";
    public const string Exit = "";
    public const string Sounds ="";
    public const string Pointer = "";
    public const string Slower = "";
    public const string Faster = "";
}

// One selectable tile. Key identifies it for focus: an app's id, or e.g. "power:sleep". A tile shows
// the first of these it has: Image, LogoPath, Glyph, or else the first letter of its title.
public sealed record TileViewModel(
    string Key,
    string Title,
    string? Subtitle,
    string? Image,
    ICommand Command,
    string? Glyph = null,
    string Accent = BrandLogos.DefaultAccent,
    string? LogoPath = null,
    string? LogoColor = null,
    string? Backdrop = null,
    string? Description = null,
    string? ActionLabel = null,
    string? IconPath = null)
{
    public string? Monogram =>
        Image == null && LogoPath == null && Glyph == null && IconPath == null && Title.Length > 0 ? Title[..1].ToUpperInvariant() : null;
}

// One key of the Search screen's keyboard.
public sealed record SearchKey(string Label, string FocusKey, ICommand Command, double Width = 88);

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
