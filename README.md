# CouchShell

A controller-first fullscreen shell for using a Windows PC from the couch. It runs on top of Windows, opens fullscreen on the TV, and is driven with an Xbox-compatible controller or a keyboard.

CouchShell is in development. The UI and the backend server both work, but they aren't connected to each other yet.

## Architecture

| Part | Location | Language | Role |
| --- | --- | --- | --- |
| CouchShell UI | `ui/` | C#, WPF | The TV interface: screens, focus navigation, controller input |
| [Tachyon](https://github.com/healers2004/Tachyon) | `vendor/tachyon/` | C++20 | The backend: a Redis-compatible server with crash-safe storage |

**CouchShell runs on Tachyon**, my standalone Redis-compatible server. Tachyon is a separate, general-purpose project with nothing specific to CouchShell in it. `vendor/tachyon/` is an unmodified copy of Tachyon v1.0.0.

## Build and test

Requires CMake, a C++20 compiler (MSVC on Windows, GCC or Clang on Linux) and the .NET 10 SDK. The UI builds on Windows only.

```
cmake -S . -B build
cmake --build build
ctest --test-dir build -C Debug --output-on-failure
dotnet test ui/CouchShell.sln
```

## Run

Start the UI with `dotnet run --project ui/CouchShell`. Use the arrow keys or D-pad to move, Enter or A to select, Escape or B to go back, and Alt+F4 to quit.

Start Tachyon with `build/vendor/tachyon/tachyon/Debug/tachyon` (on Linux, `build/vendor/tachyon/tachyon/tachyon`). It listens on 127.0.0.1:6380.

## Updating Tachyon

```
git subtree pull --prefix=vendor/tachyon https://github.com/healers2004/Tachyon.git <tag> --squash
```

Changes to the server belong in the Tachyon repository, not in this copy.
