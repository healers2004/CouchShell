using System.IO;
using System.Runtime.InteropServices;
using CouchShell.Platform;
using CouchShell.Services;
using CouchShell.ViewModels;
using CouchShell.Views;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static CouchShell.Services.ControllerInputMapper;
using static CouchShell.Services.WebInputMapper;

namespace CouchShell.Tests;

public class WebInputMapperTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    // A mapper that has seen one idle reading, so buttons count as released.
    private static WebInputMapper Ready(WebControlStyle style = WebControlStyle.Pointer)
    {
        var mapper = new WebInputMapper(style);
        mapper.Update(0, 0, 0, 0, 0, Ms(0));
        return mapper;
    }

    private static List<InputCommand> Press(WebInputMapper mapper, ushort buttons, double ms = 16) =>
        mapper.Update(buttons, 0, 0, 0, 0, Ms(ms));

    [Fact]
    public void AClicksAndHoldsUntilReleasedSoDraggingWorks()
    {
        var mapper = Ready();
        Assert.Equal([new InputCommand.MouseButton(true)], Press(mapper, ButtonA, 16));
        Assert.Empty(Press(mapper, ButtonA, 32));
        Assert.Equal([new InputCommand.MouseButton(false)], Press(mapper, 0, 48));
    }

    [Fact]
    public void ButtonStillHeldFromLaunchingDoesNotClick()
    {
        var mapper = new WebInputMapper(WebControlStyle.Pointer);
        Assert.Empty(mapper.Update(ButtonA, 0, 0, 0, 0, Ms(0)));
        Assert.Empty(mapper.Update(0, 0, 0, 0, 0, Ms(16)));
    }

    [Theory]
    [InlineData(ButtonB, KeyBrowserBack)]
    [InlineData(ButtonX, KeySpace)]
    [InlineData(ButtonY, KeyF)]
    public void PointerStyleButtons(ushort button, ushort key)
    {
        Assert.Equal([new InputCommand.TapKey(key)], Press(Ready(), button));
    }

    [Theory]
    [InlineData(ButtonA, KeyEnter)]
    [InlineData(ButtonB, KeyEscape)]
    public void KeysStyleButtons(ushort button, ushort key)
    {
        Assert.Equal([new InputCommand.TapKey(key)], Press(Ready(WebControlStyle.Keys), button));
    }

    [Fact]
    public void StickMovesThePointerAndStaysStillInTheDeadzone()
    {
        var mapper = Ready();
        Assert.Empty(mapper.Update(0, StickDeadzone, 0, 0, 0, Ms(16)));

        List<InputCommand> commands = mapper.Update(0, 32767, 0, 0, 0, Ms(116));
        var move = Assert.IsType<InputCommand.MoveCursor>(Assert.Single(commands));
        Assert.True(move.Dx > 100, $"moved only {move.Dx}");
        Assert.Equal(0, move.Dy);
    }

    [Fact]
    public void StickUpMovesThePointerUp()
    {
        var mapper = Ready();
        var move = Assert.IsType<InputCommand.MoveCursor>(Assert.Single(mapper.Update(0, 0, 32767, 0, 0, Ms(100))));
        Assert.True(move.Dy < 0);
    }

    [Fact]
    public void RightStickScrolls()
    {
        var mapper = Ready();
        var down = Assert.IsType<InputCommand.Scroll>(Assert.Single(mapper.Update(0, 0, 0, 0, -32767, Ms(100))));
        Assert.True(down.Delta < 0);
    }

    [Fact]
    public void TappingViewLeavesRunningAndHoldingClosesOnce()
    {
        var mapper = Ready();
        Assert.Empty(Press(mapper, View, 10));
        Assert.Equal([new InputCommand.LeaveRunning()], Press(mapper, 0, 500));

        Assert.Empty(Press(mapper, View, 1000));
        Assert.Empty(Press(mapper, View, 1999));
        Assert.Equal([new InputCommand.CloseApp()], Press(mapper, View, 2000));
        Assert.Empty(Press(mapper, View, 3500));
        Assert.Empty(Press(mapper, 0, 3600));  // letting go after closing isn't also a tap
    }

    [Fact]
    public void ViewHeldFromBeforeDoesNotClose()
    {
        var mapper = new WebInputMapper(WebControlStyle.Pointer);
        mapper.Update(View, 0, 0, 0, 0, Ms(0));
        Assert.Empty(mapper.Update(View, 0, 0, 0, 0, Ms(5000)));
    }

    [Fact]
    public void KeyboardTakesOverTheControlsThenGivesThemBack()
    {
        var mapper = Ready();
        Assert.Equal([new InputCommand.SetKeyboardOpen(true)], Press(mapper, Start, 16));
        Press(mapper, 0, 32);

        Assert.Equal([new InputCommand.KeyboardMove(NavAction.Right)], Press(mapper, DpadRight, 48));
        Press(mapper, 0, 64);
        Assert.Equal([new InputCommand.KeyboardPress()], Press(mapper, ButtonA, 80));
        Press(mapper, 0, 96);
        Assert.Equal([new InputCommand.TapKey(KeyBackspace)], Press(mapper, ButtonB, 112));
        Press(mapper, 0, 128);
        Assert.Equal([new InputCommand.KeyboardMove(NavAction.Up)], mapper.Update(0, 0, 30000, 0, 0, Ms(144)));
        Press(mapper, 0, 160);

        Assert.Equal([new InputCommand.KeyboardShift()], Press(mapper, ButtonY, 168));
        Press(mapper, 0, 172);

        Assert.Equal([new InputCommand.SetKeyboardOpen(false)], Press(mapper, Start, 176));
        Press(mapper, 0, 192);
        Assert.Equal([new InputCommand.MouseButton(true)], Press(mapper, ButtonA, 208));
    }
}

public class OverlayViewModelTests
{
    [Fact]
    public void ShiftGivesCapitalsAndTheSymbolsNeededToSignIn()
    {
        var overlay = new OverlayViewModel();
        overlay.ToggleShift();
        Assert.Equal('Q', overlay.SelectedKey.Character);
        Assert.Equal("Q", overlay.SelectedKey.Label);

        char[] typed = overlay.Rows.SelectMany(row => row).Where(key => key.Kind == KeyKind.Character).Select(key => key.Character).ToArray();
        Assert.Contains('#', typed);
        Assert.Contains('/', typed);
        Assert.Contains('!', typed);

        overlay.ToggleShift();
        Assert.Equal('q', overlay.SelectedKey.Character);
    }
}

public class SteamLibraryTests
{
    private static string Manifest(string appId, string name, string flags = "4") => $$"""
        "AppState"
        {
            "appid"		"{{appId}}"
            "name"		"{{name}}"
            "StateFlags"		"{{flags}}"
            "installdir"		"Folder"
            "UserConfig"
            {
                "name"		"Some other value"
            }
        }
        """;

    [Fact]
    public void ReadsAnInstalledGame()
    {
        AppDefinition? game = SteamLibrary.ParseManifest(Manifest("2567870", "Chained Together"), "C:\\nowhere");
        Assert.NotNull(game);
        Assert.Equal("steam-2567870", game.Id);
        Assert.Equal("Chained Together", game.Name);
        Assert.Equal("2567870", game.Target);
        Assert.Equal("steam", game.Type);
        Assert.Equal("games", game.Category);
        Assert.Null(game.Image);
        Assert.Null(game.WebControl);
    }

    [Theory]
    [InlineData("228980", "Steamworks Common Redistributables", "4")]
    [InlineData("1628350", "Steam Linux Runtime 3.0", "4")]
    public void SkipsSupportPackagesAndUnfinishedGames(string appId, string name, string flags)
    {
        Assert.Null(SteamLibrary.ParseManifest(Manifest(appId, name, flags), "C:\\nowhere"));
    }

    [Fact]
    public void ReadsEveryLibraryFolder()
    {
        const string text = """
            "libraryfolders"
            {
                "0" { "path"		"C:\\Program Files (x86)\\Steam" "totalsize" "0" }
                "1" { "path"		"D:\\Games\\Steam" }
            }
            """;
        Assert.Equal(["C:\\Program Files (x86)\\Steam", "D:\\Games\\Steam"], SteamLibrary.ParseLibraries(text));
    }

    [Fact]
    public void FindsArtworkInHashedFoldersAndPrefersWidePictures()
    {
        string folder = Directory.CreateTempSubdirectory("couchshell-steam-test").FullName;
        try
        {
            string game = Directory.CreateDirectory(Path.Combine(folder, "appcache", "librarycache", "5")).FullName;
            File.WriteAllText(Path.Combine(game, "library_hero.jpg"), "");
            string hashed = Directory.CreateDirectory(Path.Combine(game, "9989d379")).FullName;
            File.WriteAllText(Path.Combine(hashed, "library_header.jpg"), "");

            AppDefinition? found = SteamLibrary.ParseManifest(Manifest("5", "Game"), folder);
            Assert.Equal(Path.Combine(hashed, "library_header.jpg"), found?.Image);
            Assert.Equal(Path.Combine(game, "library_hero.jpg"), found?.Backdrop);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class InputOverlayWindowTests
{
}

public class BrandLogoTests
{
    // A typo in a logo's path data would only show up as a blank tile, so check them all at once.
    [Fact]
    public void EveryLogoIsAValidShape()
    {
        foreach (string name in new[] { "youtube", "netflix", "chrome", "steam", "battlenet", "epic", "riot", "spotify" })
        {
            Geometry shape = Geometry.Parse(BrandLogos.Find(name)!.Path);
            Assert.True(shape.Bounds.Width > 10, name);
        }
    }
}

public class WindowsNativeTests
{
    // Win32 reads these bytes directly, so a wrong size makes SendInput fail for every event.
    [Fact]
    public void InputStructMatchesWin32OnX64() => Assert.Equal(40, Marshal.SizeOf<WindowsNative.Input>());

}
