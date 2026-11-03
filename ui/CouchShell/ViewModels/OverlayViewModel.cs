using CouchShell.Services;

namespace CouchShell.ViewModels;

public enum KeyKind { Character, Text, Space, Backspace, Enter, Shift }

public sealed class KeyViewModel(string label, KeyKind kind, char character = '\0', char shifted = '\0') : ViewModelBase
{
    private bool _isSelected;
    private bool _isShifted;

    public KeyKind Kind { get; } = kind;
    public bool IsWide => Kind != KeyKind.Character;
    public char Character => _isShifted ? shifted : character;
    public string Label => Kind == KeyKind.Character ? Character.ToString() : label;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public bool IsShifted
    {
        get => _isShifted;
        set
        {
            _isShifted = value;
            Notify(nameof(Label));
        }
    }
}

// What floats over a running web app: the controls hint and the on-screen keyboard.
public sealed class OverlayViewModel : ViewModelBase
{
    // Each row with what Shift turns it into, enough for email addresses and passwords.
    private static readonly (string Normal, string Shifted)[] CharacterRows =
    [
        ("1234567890", "!@#$%^&*()"),
        ("qwertyuiop", "QWERTYUIOP"),
        ("asdfghjkl-", "ASDFGHJKL'"),
        ("zxcvbnm.@_", "ZXCVBNM,:/"),
    ];

    private bool _isHintVisible;
    private bool _isKeyboardVisible;
    private bool _isWaiting;
    private string _hint = "";
    private bool _isShift;
    private int _row;
    private int _column;

    public OverlayViewModel()
    {
        var rows = CharacterRows
            .Select(row => row.Normal.Zip(row.Shifted, (normal, shifted) => new KeyViewModel("", KeyKind.Character, normal, shifted)).ToList())
            .ToList();
        rows.Add(
        [
            new KeyViewModel("Shift", KeyKind.Shift),
            new KeyViewModel(".com", KeyKind.Text),
            new KeyViewModel("Space", KeyKind.Space),
            new KeyViewModel("Delete", KeyKind.Backspace),
            new KeyViewModel("Enter", KeyKind.Enter),
        ]);
        Rows = rows;
        ResetSelection();
    }

    public IReadOnlyList<List<KeyViewModel>> Rows { get; }

    public KeyViewModel SelectedKey => Rows[_row][_column];

    public string Hint
    {
        get => _hint;
        private set => Set(ref _hint, value);
    }

    public bool IsHintVisible
    {
        get => _isHintVisible;
        set
        {
            Set(ref _isHintVisible, value);
            Notify(nameof(ShowHint));
        }
    }

    public bool IsKeyboardVisible
    {
        get => _isKeyboardVisible;
        set
        {
            Set(ref _isKeyboardVisible, value);
            Notify(nameof(ShowHint));
        }
    }

    // The keyboard lists its own controls, so the bar steps aside while it's open.
    public bool ShowHint => IsHintVisible && !IsKeyboardVisible;

    // waitingFor names what is on screen before the app starts: its launcher, like Steam.
    public void ShowHintFor(WebControlStyle style, bool canLeaveRunning = false, string? waitingFor = null)
    {
        IsWaiting = style == WebControlStyle.WaitingForLaunch;
        Hint = style switch
        {
            WebControlStyle.Keys => "D-pad: move    A: select    B: back    Start: keyboard    Hold View: exit    Press right stick: hide this",
            WebControlStyle.WaitingForLaunch => $"Waiting for {waitingFor ?? "the app"}    Left stick: pointer    A: click    Hold View: cancel    Press right stick: hide this",
            _ when canLeaveRunning => "Left stick: pointer    A: click    B: back    Right stick: scroll    Start: keyboard    Tap View: home, keeps running    Hold View: close    Press right stick: hide this",
            _ => "Left stick: pointer    A: click    B: back    Right stick: scroll    X: play/pause    Y: fullscreen    Start: keyboard    Hold View: exit    Press right stick: hide this",
        };
        IsHintVisible = true;
    }

    // A launcher is up before the game: the bar shows a spinner.
    public bool IsWaiting
    {
        get => _isWaiting;
        private set => Set(ref _isWaiting, value);
    }

    public void ToggleShift()
    {
        _isShift = !_isShift;
        foreach (KeyViewModel key in Rows.SelectMany(row => row)) key.IsShifted = _isShift;
    }

    public void ResetSelection()
    {
        if (_isShift) ToggleShift();
        SelectedKey.IsSelected = false;
        _row = 1;
        _column = 0;
        SelectedKey.IsSelected = true;
    }

    // Moves the highlight, wrapping at the edges. Rows differ in length, so moving between them
    // keeps the same relative position rather than the same column number.
    public void Move(NavAction direction)
    {
        SelectedKey.IsSelected = false;
        switch (direction)
        {
            case NavAction.Left:
                _column = (_column + Rows[_row].Count - 1) % Rows[_row].Count;
                break;
            case NavAction.Right:
                _column = (_column + 1) % Rows[_row].Count;
                break;
            case NavAction.Up:
                ChangeRow((_row + Rows.Count - 1) % Rows.Count);
                break;
            case NavAction.Down:
                ChangeRow((_row + 1) % Rows.Count);
                break;
        }
        SelectedKey.IsSelected = true;
    }

    private void ChangeRow(int row)
    {
        double position = (_column + 0.5) / Rows[_row].Count;
        _row = row;
        _column = Math.Min((int)(position * Rows[_row].Count), Rows[_row].Count - 1);
    }
}
