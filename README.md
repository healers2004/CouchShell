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

To start CouchShell at logon, run `powershell -ExecutionPolicy Bypass -File tools\install-startup.ps1`. To stop that, add `-Remove` to the end of the same command.

Data and logs are in `%LOCALAPPDATA%\CouchShell`.

## Controls

| Action | Controller | Keyboard |
| --- | --- | --- |
| Move | D-pad or left stick | Arrow keys |
| Select | A | Enter |
| Back | B | Escape |
| Volume down, up | LB, RB | `-`, `=` |
| Mute | Y | M |
| Quit | | Alt+F4 |

## Adding apps

Apps are defined in `apps.json`. CouchShell reads `%LOCALAPPDATA%\CouchShell\apps.json` if it exists, otherwise `config/apps.json` next to the program. Changes apply the next time the UI starts, with no recompiling. A malformed entry is logged and skipped.

| Field | Meaning |
| --- | --- |
| `id` | Lowercase letters, digits, `-` and `_` |
| `name` | The tile's title |
| `category` | `games`, `watch` or `apps` |
| `type` | `exe`, `web` or `steam` |
| `target` | Executable path (environment variables allowed), web address, or Steam app id |
| `args` | Optional: command line for an `exe` |
| `processName` | Required for `steam`, optional for `exe`: the process to watch, when it isn't the one started directly |
| `image` | Optional: picture for the tile, relative to `apps.json` |

Web apps open in Edge or Chrome (chosen under Settings) in app mode, with their own browser profile.

## Updating Tachyon

```
git subtree pull --prefix=vendor/tachyon https://github.com/healers2004/Tachyon.git <tag> --squash
```

Changes to the server belong in the Tachyon repository, not in this copy.
