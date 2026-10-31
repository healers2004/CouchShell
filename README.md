# CouchShell

A controller-first fullscreen shell for using a Windows PC from the couch. It runs on top of Windows, opens fullscreen on the TV, and is driven with an Xbox-compatible controller or a keyboard. Pick a tile, use the app or game, close it, and you're back on the same tile.

## Architecture

| Part | Location | Language | Role |
| --- | --- | --- | --- |
| CouchShell UI | `ui/` | C#, WPF | The TV interface: screens, focus navigation, controller input |
| [Tachyon](https://github.com/healers2004/Tachyon) | `vendor/tachyon/` | C++20 | The backend: a Redis-compatible server with crash-safe storage |
| couchd | `couchd/` | C++20 | Background helper: launches apps, watches them, controls volume and power |

**CouchShell runs on Tachyon**, my standalone Redis-compatible server. Tachyon is a separate, general-purpose project with nothing specific to CouchShell in it. `vendor/tachyon/` is an unmodified copy of Tachyon v1.0.0.

The UI and couchd never talk to each other directly. They're both ordinary Redis clients of Tachyon, using only standard commands:

- **Shared state** is stored in Tachyon: app definitions in hashes, recent items and play history in lists.
- **Requests and events** use pub/sub. The UI publishes `launch youtube` on `couch:requests`; couchd starts it and publishes `started youtube` on `couch:events`, then `exited youtube` when it closes.

So the UI can crash mid-game without losing anything. Restart it, and it reconnects, sees what's running and carries on. And data survives a power cut, because Tachyon writes it to its log before acknowledging it.

## Build and test

Requires CMake, a C++20 compiler (MSVC on Windows, GCC or Clang on Linux) and the .NET 10 SDK. The UI and the couchd program are Windows-only; couchd's logic and Tachyon also build and test on Linux.

```
cmake -S . -B build
cmake --build build --config Debug
ctest --test-dir build -C Debug --output-on-failure
dotnet test ui/CouchShell.sln
```

End-to-end check of Tachyon and couchd together, on Windows:

```
powershell -ExecutionPolicy Bypass -File tools\integration_test.ps1 build\vendor\tachyon\tachyon\Debug\tachyon.exe build\couchd\Debug\couchd.exe
```

## Run

Build a Release, then start all three programs:

```
cmake --build build --config Release
dotnet build ui/CouchShell.sln -c Release
powershell -ExecutionPolicy Bypass -File tools\start-couchshell.ps1
```

Tachyon and couchd run in the background with no windows. To stop them, run `tools\stop-couchshell.ps1` the same way. For a Debug build, add `-Configuration Debug` to the start command.

Settings has the interface sound style (Soft, Click, Chime or off), and the pointer speed (Slow, Medium, High, or finer steps). To start CouchShell at logon, run `powershell -ExecutionPolicy Bypass -File tools\install-startup.ps1`. To stop that, add `-Remove` to the end of the same command.

Data and logs are in `%LOCALAPPDATA%\CouchShell`.

## Controls

In CouchShell's menus:

| Action | Controller | Keyboard |
| --- | --- | --- |
| Move | D-pad or left stick | Arrow keys |
| Select | A | Enter |
| Back | B | Escape |
| Volume down, up | LB, RB | `-`, `=` |
| Mute | Y | M |
| Quit | | Alt+F4 |

While a web app is running, the controller drives it. Games aren't affected: they read the controller themselves.

| Action | Controller |
| --- | --- |
| Move the pointer | Left stick |
| Click | A |
| Back a page | B |
| Scroll | Right stick |
| Arrow keys | D-pad |
| Play or pause (Space) | X |
| Fullscreen (F) | Y |
| Volume down, up | LB, RB |
| On-screen keyboard | Start |
| Exit the app | Hold View for a second |
| Back to CouchShell, leaving it running (not web apps) | Tap View |
| Show or hide the controls bar | Press the right stick |

If Steam shows something before a game starts, like a launch options dialog, CouchShell steps aside and the pointer controls work on it. Holding View cancels the launch.

Sites built for TVs can use `"controller": "keys"` instead, where the D-pad and stick navigate, A is Enter and B is Escape. YouTube ships this way, using its TV interface.

## Search

The Search tab opens anything in Windows' All apps list, including Store apps. Type on its keyboard and pick a result. The pointer controls work in whatever opens, and holding View closes it and comes back. B deletes a letter while searching.

## Adding apps

Installed games appear automatically: Steam (with artwork, from every Steam library), Riot Games, Battle.net and Epic Games. The launchers get tiles of their own under Apps, for browsing and installing from the couch.

Other apps are defined in `apps.json`. CouchShell loads `config/apps.json` next to the program, plus your own additions from `%LOCALAPPDATA%\CouchShell\apps.json`, which appear first and can replace a built-in app by reusing its id. Changes apply the next time the UI starts, with no recompiling. A malformed entry is logged and skipped.

| Field | Meaning |
| --- | --- |
| `id` | Lowercase letters, digits, `-` and `_` |
| `name` | The tile's title |
| `category` | `games`, `watch` or `apps` |
| `type` | `exe`, `web`, `steam` or `shell` |
| `target` | Executable path (environment variables allowed), web address, Steam app id, or `shell:AppsFolder\\<id>` from Windows' All apps list |
| `args` | Optional: command line for an `exe` |
| `processName` | Optional: the process to watch, when it isn't the one started directly |
| `image` | Optional: picture for the tile, relative to `apps.json` |
| `controller` | Optional, for `web`: `pointer` (the default) or `keys` |
| `userAgent` | Optional, for `web`: what the browser tells the site it is |

| `logo` | Optional: a built-in logo: `youtube`, `netflix`, `chrome`, `spotify`, `steam`, `battlenet`, `epic` or `riot` |
| `requires` | Optional: a file that must exist for the tile to appear |
| `color` | Optional: the tile's colour, like `#0E7C90` |
| `window` | Optional, for `web`: `browser` for a normal browser window with an address bar |
| `installFolder` | Optional: the app counts as running while a program from this folder is |
| `watch` | Optional, with `installFolder`: `window` to count only while it has a window open, for launchers that sit in the tray |

Web apps open in Chrome or Edge (chosen under Settings, Chrome by default) with their own browser profile. Programs that aren't installed are left out.

Logo shapes are from [Simple Icons](https://simpleicons.org) (CC0). The marks belong to their owners and are used only to identify their services.

## Updating Tachyon

```
git subtree pull --prefix=vendor/tachyon https://github.com/healers2004/Tachyon.git <tag> --squash
```

Changes to the server belong in the Tachyon repository, not in this copy.
