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

    [Fact]
    public void ReleaseLetsGoOfAHeldMouseButton()
    {
        var mapper = Ready();
        Press(mapper, ButtonA);
        Assert.Equal([new InputCommand.MouseButton(false)], mapper.Release());
        Assert.Empty(mapper.Release());
    }

    [Theory]
    [InlineData(ButtonB, KeyBrowserBack)]
    [InlineData(ButtonX, KeySpace)]
    [InlineData(ButtonY, KeyF)]
    [InlineData(DpadUp, KeyUp)]
    [InlineData(DpadLeft, KeyLeft)]
    public void PointerStyleButtons(ushort button, ushort key)
    {
        Assert.Equal([new InputCommand.TapKey(key)], Press(Ready(), button));
    }

    [Theory]
    [InlineData(ButtonA, KeyEnter)]
    [InlineData(ButtonB, KeyEscape)]
    [InlineData(DpadRight, KeyRight)]
    public void KeysStyleButtons(ushort button, ushort key)
    {
        Assert.Equal([new InputCommand.TapKey(key)], Press(Ready(WebControlStyle.Keys), button));
    }

    [Fact]
    public void KeysStyleStickNavigatesInsteadOfMovingAPointer()
    {
        var mapper = Ready(WebControlStyle.Keys);
        Assert.Equal([new InputCommand.TapKey(KeyRight)], mapper.Update(0, 30000, 0, 0, 0, Ms(16)));
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
    public void WaitingForLaunchUsesThePointerSoSteamDialogsWork()
    {
        var mapper = Ready(WebControlStyle.WaitingForLaunch);
        Assert.IsType<InputCommand.MoveCursor>(Assert.Single(mapper.Update(0, 32767, 0, 0, 0, Ms(100))));
        Assert.Equal([new InputCommand.MouseButton(true)], Press(mapper, ButtonA, 116));
    }

    [Fact]
    public void StickUpMovesThePointerUp()
    {
        var mapper = Ready();
        var move = Assert.IsType<InputCommand.MoveCursor>(Assert.Single(mapper.Update(0, 0, 32767, 0, 0, Ms(100))));
        Assert.True(move.Dy < 0);
    }

    [Fact]
    public void SmallTiltsMoveSlowlyAndAddUp()
    {
        var mapper = Ready();
        int total = 0;
        for (int i = 1; i <= 200; i++)
        {
            foreach (InputCommand command in mapper.Update(0, 10000, 0, 0, 0, Ms(i * 16)))
            {
                total += ((InputCommand.MoveCursor)command).Dx;
            }
        }
        Assert.InRange(total, 1, 500);  // moved, but much slower than the 4800 px of full tilt
    }

    [Fact]
    public void RightStickScrolls()
    {
        var mapper = Ready();
        var down = Assert.IsType<InputCommand.Scroll>(Assert.Single(mapper.Update(0, 0, 0, 0, -32767, Ms(100))));
        Assert.True(down.Delta < 0);
    }

    [Fact]
    public void PauseLongerThanAFrameDoesNotJumpThePointer()
    {
        var mapper = Ready();
        var move = Assert.IsType<InputCommand.MoveCursor>(Assert.Single(mapper.Update(0, 32767, 0, 0, 0, Ms(60000))));
        Assert.InRange(move.Dx, 1, 151);
    }

    [Fact]
    public void PointerSpeedScalesHowFarTheStickMoves()
    {
        InputCommand.MoveCursor Move(int percent)
        {
            var mapper = new WebInputMapper(WebControlStyle.Pointer, percent);
            mapper.Update(0, 0, 0, 0, 0, Ms(0));
            return Assert.IsType<InputCommand.MoveCursor>(Assert.Single(mapper.Update(0, 32767, 0, 0, 0, Ms(100))));
        }

        Assert.Equal(150, Move(100).Dx);
        Assert.Equal(90, Move(60).Dx);
        Assert.Equal(45, Move(30).Dx);
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

    [Fact]
    public void PressingTheRightStickTogglesTheControlsBarEachPress()
    {
        var mapper = Ready();
        Assert.Equal([new InputCommand.ToggleControlsBar()], Press(mapper, RightStickPress, 16));
        Assert.Empty(Press(mapper, RightStickPress, 2000));  // holding doesn't repeat
        Press(mapper, 0, 2016);
        Assert.Equal([new InputCommand.ToggleControlsBar()], Press(mapper, RightStickPress, 2032));
    }

    [Fact]
    public void ShouldersStillControlVolume()
    {
        Assert.Equal([new InputCommand.Volume(NavAction.VolumeUp)], Press(Ready(), RightShoulder));
    }
}

public class OverlayViewModelTests
{
    [Fact]
    public void StartsOnALetterWithExactlyOneKeySelected()
    {
        var overlay = new OverlayViewModel();
        Assert.Equal('q', overlay.SelectedKey.Character);
        Assert.Single(overlay.Rows.SelectMany(row => row), key => key.IsSelected);
    }

    [Fact]
    public void MovingWrapsAndKeepsOneKeySelected()
    {
        var overlay = new OverlayViewModel();
        overlay.Move(NavAction.Left);
        Assert.Equal('p', overlay.SelectedKey.Character);
        overlay.Move(NavAction.Right);
        Assert.Equal('q', overlay.SelectedKey.Character);
        overlay.Move(NavAction.Up);
        Assert.Equal('1', overlay.SelectedKey.Character);
        overlay.Move(NavAction.Up);
        Assert.Equal(KeyKind.Shift, overlay.SelectedKey.Kind);
        Assert.Single(overlay.Rows.SelectMany(row => row), key => key.IsSelected);
    }

    [Fact]
    public void MovingToTheShortRowKeepsTheRelativePosition()
    {
        var overlay = new OverlayViewModel();
        overlay.Move(NavAction.Left);  // 'p', far right
        overlay.Move(NavAction.Down);
        overlay.Move(NavAction.Down);
        overlay.Move(NavAction.Down);
        Assert.Equal(KeyKind.Enter, overlay.SelectedKey.Kind);
    }

    [Fact]
    public void TheBarOnlyOffersLeavingRunningWhenItWorks()
    {
        var overlay = new OverlayViewModel();
        overlay.ShowHintFor(WebControlStyle.Pointer, canLeaveRunning: true);
        Assert.Contains("Tap View", overlay.Hint);
        overlay.ShowHintFor(WebControlStyle.Pointer);
        Assert.DoesNotContain("Tap View", overlay.Hint);
    }

    [Fact]
    public void ControlsBarStepsAsideForTheKeyboardAndComesBack()
    {
        var overlay = new OverlayViewModel();
        overlay.ShowHintFor(WebControlStyle.Pointer);
        Assert.True(overlay.ShowHint);
        Assert.Contains("hide this", overlay.Hint);

        overlay.IsKeyboardVisible = true;
        Assert.False(overlay.ShowHint);
        overlay.IsKeyboardVisible = false;
        Assert.True(overlay.ShowHint);

        overlay.IsHintVisible = false;
        Assert.False(overlay.ShowHint);
    }

    [Fact]
    public void AtSignNeedsNoShift()
    {
        var overlay = new OverlayViewModel();
        Assert.Contains(overlay.Rows.SelectMany(row => row), key => key.Character == '@' && key.Label == "@");
    }

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

    [Fact]
    public void ANewSessionStartsUnshifted()
    {
        var overlay = new OverlayViewModel();
        overlay.ToggleShift();
        overlay.ResetSelection();
        Assert.Equal('q', overlay.SelectedKey.Character);
    }

    [Fact]
    public void ResetReturnsToTheStart()
    {
        var overlay = new OverlayViewModel();
        overlay.Move(NavAction.Down);
        overlay.ResetSelection();
        Assert.Equal('q', overlay.SelectedKey.Character);
        Assert.Single(overlay.Rows.SelectMany(row => row), key => key.IsSelected);
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
    [InlineData("2348590", "Proton 8.0", "4")]
    [InlineData("100", "Still Downloading", "1026")]
    [InlineData("431960", "Wallpaper Engine", "4")]
    [InlineData("abc", "Bad Id", "4")]
    public void SkipsSupportPackagesAndUnfinishedGames(string appId, string name, string flags)
    {
        Assert.Null(SteamLibrary.ParseManifest(Manifest(appId, name, flags), "C:\\nowhere"));
    }

    [Fact]
    public void RemembersWhereTheGameIsInstalledInItsOwnLibrary()
    {
        AppDefinition? game = SteamLibrary.ParseManifest(Manifest("1", "Game"), "C:\\Steam", "D:\\Games");
        Assert.Equal("D:\\Games\\steamapps\\common\\Folder", game?.InstallFolder);
    }

    [Fact]
    public void MalformedManifestIsSkipped()
    {
        Assert.Null(SteamLibrary.ParseManifest("not a manifest", "C:\\nowhere"));
        Assert.Null(SteamLibrary.ParseManifest("", "C:\\nowhere"));
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
    public void ScansAFolderSortsByNameAndSurvivesAMissingFolder()
    {
        string folder = Directory.CreateTempSubdirectory("couchshell-steam-test").FullName;
        try
        {
            string steamApps = Directory.CreateDirectory(Path.Combine(folder, "steamapps")).FullName;
            File.WriteAllText(Path.Combine(steamApps, "appmanifest_2.acf"), Manifest("2", "Zebra"));
            File.WriteAllText(Path.Combine(steamApps, "appmanifest_1.acf"), Manifest("1", "Apple"));
            File.WriteAllText(Path.Combine(steamApps, "libraryfolders.vdf"), "\"0\" { \"path\" \"Z:\\\\missing\" }");

            string art = Directory.CreateDirectory(Path.Combine(folder, "appcache", "librarycache", "1")).FullName;
            File.WriteAllText(Path.Combine(art, "header.jpg"), "");

            var log = new List<string>();
            List<AppDefinition> games = SteamLibrary.Scan(folder, log.Add);
            Assert.Equal(["Apple", "Zebra"], games.Select(game => game.Name));
            Assert.EndsWith("header.jpg", games[0].Image);
            Assert.Null(games[1].Image);
            Assert.Empty(log);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
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

    [Fact]
    public void NoSteamInstalledGivesNoGames()
    {
        Assert.Empty(SteamLibrary.Scan("C:\\does\\not\\exist", _ => { }));
    }
}

public class InputOverlayWindowTests
{
    // Loads the real XAML and draws it with the keyboard open. A bad binding or resource throws
    // here instead of when someone is on the couch. Also leaves a picture to look at.
    [Fact]
    public void LoadsAndDrawsTheKeyboard()
    {
        Exception? failure = null;
        string picture = Path.Combine(Path.GetTempPath(), "couchshell-overlay.png");
        var thread = new Thread(() =>
        {
            try
            {
                var overlay = new OverlayViewModel { IsKeyboardVisible = true };
                overlay.ShowHintFor(WebControlStyle.Pointer);
                var window = new InputOverlayWindow { DataContext = overlay };
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1920, 1080));
                content.Arrange(new Rect(0, 0, 1920, 1080));
                content.UpdateLayout();

                var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using FileStream file = File.Create(picture);
                encoder.Save(file);
                window.Close();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);  // WPF windows require this kind of thread
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.True(new FileInfo(picture).Length > 1000);
    }
}

public class BrandLogoTests
{
    [Theory]
    [InlineData("youtube")]
    [InlineData("netflix")]
    [InlineData("chrome")]
    [InlineData("steam")]
    [InlineData("battlenet")]
    [InlineData("epic")]
    [InlineData("riot")]
    [InlineData("spotify")]
    public void EveryLogoIsAValidShape(string name)
    {
        Geometry shape = Geometry.Parse(BrandLogos.Find(name)!.Path);
        Assert.False(shape.Bounds.IsEmpty);
        Assert.True(shape.Bounds.Width > 10);
    }

    [Fact]
    public void UnknownLogoIsNull() => Assert.Null(BrandLogos.Find("nope"));
}

public class WindowsNativeTests
{
    // Win32 reads these bytes directly, so a wrong size makes SendInput fail for every event.
    [Fact]
    public void InputStructMatchesWin32OnX64() => Assert.Equal(40, Marshal.SizeOf<WindowsNative.Input>());

}
