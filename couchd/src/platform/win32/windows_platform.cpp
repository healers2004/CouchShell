#include "platform/win32/windows_platform.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

// These need the full windows.h first.
#include <endpointvolume.h>
#include <mmdeviceapi.h>
#include <powrprof.h>
#include <shellapi.h>
#include <tlhelp32.h>
#include <wrl/client.h>

#include <algorithm>
#include <chrono>
#include <cwchar>
#include <iostream>
#include <vector>

using Microsoft::WRL::ComPtr;  // COM's RAII pointer: calls Release() when it goes out of scope

namespace couchd {

namespace {

// Win32 text is UTF-16; everything from Tachyon is UTF-8. Returns "" for empty or invalid text.
std::wstring to_wide(const std::string& text) {
    if (text.empty()) return L"";
    int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(),
                                   static_cast<int>(text.size()), nullptr, 0);
    if (size == 0) return L"";
    std::wstring wide(static_cast<std::size_t>(size), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()),
                        wide.data(), size);
    return wide;
}

// Expands %LOCALAPPDATA% and friends. Returns the input unchanged if that fails.
std::wstring expand_environment(const std::wstring& text) {
    DWORD size = ExpandEnvironmentStringsW(text.c_str(), nullptr, 0);
    if (size == 0) return text;
    std::wstring expanded(size, L'\0');
    if (ExpandEnvironmentStringsW(text.c_str(), expanded.data(), size) == 0) return text;
    expanded.resize(size - 1);  // size counts the terminating null
    return expanded;
}

bool file_exists(const std::wstring& path) {
    DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
}

// Starts `path` with `command_line`. Returns the process handle, or nullptr with `error` set.
void* start_process(const std::wstring& path, std::wstring command_line, std::string& error) {
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION info{};

    // Run it from its own folder, as if it had been double-clicked.
    std::wstring directory = path.substr(0, path.find_last_of(L"\\/"));

    // CreateProcessW may modify the command line, so it needs a writable buffer: data() of our
    // own copy. Returns 0 on failure, with the reason in GetLastError().
    if (!CreateProcessW(path.c_str(), command_line.data(), nullptr, nullptr, FALSE, 0, nullptr,
                        directory.c_str(), &startup, &info)) {
        error = "Windows could not start it (error " + std::to_string(GetLastError()) + ").";
        return nullptr;
    }
    CloseHandle(info.hThread);  // only the process handle is needed
    return info.hProcess;
}

// Edge and Chrome install to fixed places. Returns "" if neither is present.
std::wstring find_browser(const std::string& preferred) {
    const std::vector<std::wstring> edge = {
        L"%ProgramFiles(x86)%\\Microsoft\\Edge\\Application\\msedge.exe",
        L"%ProgramFiles%\\Microsoft\\Edge\\Application\\msedge.exe",
    };
    const std::vector<std::wstring> chrome = {
        L"%ProgramFiles%\\Google\\Chrome\\Application\\chrome.exe",
        L"%ProgramFiles(x86)%\\Google\\Chrome\\Application\\chrome.exe",
        L"%LOCALAPPDATA%\\Google\\Chrome\\Application\\chrome.exe",
    };
    const std::vector<std::wstring>& first = preferred == "edge" ? edge : chrome;
    const std::vector<std::wstring>& second = preferred == "edge" ? chrome : edge;
    for (const std::wstring& candidate : first) {
        std::wstring path = expand_environment(candidate);
        if (file_exists(path)) return path;
    }
    for (const std::wstring& candidate : second) {
        std::wstring path = expand_environment(candidate);
        if (file_exists(path)) return path;
    }
    return L"";
}

// Opens the first process named `name`. Returns nullptr if there's none.
void* open_process_by_name(const std::wstring& name) {
    // A snapshot of every running process. INVALID_HANDLE_VALUE on failure.
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return nullptr;
    UniqueHandle snapshot_owner(snapshot);

    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (!Process32FirstW(snapshot, &entry)) return nullptr;
    do {
        if (_wcsicmp(entry.szExeFile, name.c_str()) == 0) {
            // SYNCHRONIZE is the only right needed to wait on it. nullptr if access is denied.
            HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, entry.th32ProcessID);
            if (process != nullptr) return process;
        }
    } while (Process32NextW(snapshot, &entry));
    return nullptr;
}

// The default output device's volume control. Asked for on every call, so a change of default
// device (e.g. plugging in the TV) just works. COM calls return an HRESULT; FAILED() checks it.
bool audio_endpoint(ComPtr<IAudioEndpointVolume>& endpoint) {
    ComPtr<IMMDeviceEnumerator> enumerator;
    if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                IID_PPV_ARGS(&enumerator)))) {
        return false;
    }
    ComPtr<IMMDevice> device;
    if (FAILED(enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device))) return false;
    return SUCCEEDED(device->Activate(__uuidof(IAudioEndpointVolume), CLSCTX_ALL, nullptr,
                                      reinterpret_cast<void**>(endpoint.GetAddressOf())));
}

// Steam records each game's state in the registry while it runs, which saves configuring which
// process belongs to which game. RegGetValueW returns ERROR_SUCCESS or an error code.
bool steam_says_running(const std::wstring& app_id) {
    const std::wstring key = L"Software\\Valve\\Steam\\Apps\\" + app_id;
    DWORD running = 0;
    DWORD size = sizeof(running);
    LSTATUS status = RegGetValueW(HKEY_CURRENT_USER, key.c_str(), L"Running", RRF_RT_REG_DWORD,
                                  nullptr, &running, &size);
    return status == ERROR_SUCCESS && running != 0;
}

// What find_app_window is looking for, and what it found.
struct WindowSearch {
    DWORD process_id = 0;
    std::wstring folder;  // with a trailing backslash; empty if searching by process id alone
    HWND found = nullptr;
};

// True if the process's program lives inside `folder`. Fails safe: false if it can't tell.
bool process_is_in_folder(DWORD process_id, const std::wstring& folder) {
    UniqueHandle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id));
    if (!process) return false;
    wchar_t path[MAX_PATH * 2];
    DWORD size = MAX_PATH * 2;
    if (!QueryFullProcessImageNameW(process.get(), 0, path, &size)) return false;
    return _wcsnicmp(path, folder.c_str(), folder.size()) == 0;
}

// EnumWindows calls this for each top-level window; returning FALSE stops the search.
BOOL CALLBACK check_window(HWND window, LPARAM search_address) {
    WindowSearch* search = reinterpret_cast<WindowSearch*>(search_address);
    // A real app window: visible, not owned by another window, and with a title.
    if (!IsWindowVisible(window) || GetWindow(window, GW_OWNER) != nullptr ||
        GetWindowTextLengthW(window) == 0) {
        return TRUE;
    }
    DWORD owner = 0;
    GetWindowThreadProcessId(window, &owner);
    if ((search->process_id != 0 && owner == search->process_id) ||
        (!search->folder.empty() && process_is_in_folder(owner, search->folder))) {
        search->found = window;
        return FALSE;
    }
    return TRUE;
}

// Asks for the foreground. Windows refuses most background programs, unless they share input with
// the foreground thread or keyboard input has just happened, so this tries both, tapping Alt only
// when needed. Alt on its own types nothing.
void take_foreground(HWND window) {
    if (IsIconic(window)) ShowWindow(window, SW_RESTORE);
    for (int attempt = 0; attempt < 2 && GetForegroundWindow() != window; ++attempt) {
        if (attempt == 1) {
            keybd_event(VK_MENU, 0, 0, 0);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
        }
        DWORD ours = GetCurrentThreadId();
        DWORD theirs = GetWindowThreadProcessId(GetForegroundWindow(), nullptr);
        bool attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, TRUE);
        BringWindowToTop(window);
        SetForegroundWindow(window);
        if (attached) AttachThreadInput(ours, theirs, FALSE);
    }
}

// The full path of a process's program, or "" if Windows won't say.
std::wstring process_path(DWORD process_id) {
    UniqueHandle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id));
    if (!process) return L"";
    wchar_t path[MAX_PATH * 2];
    DWORD size = MAX_PATH * 2;
    if (!QueryFullProcessImageNameW(process.get(), 0, path, &size)) return L"";
    return std::wstring(path, size);
}

// CouchShell's own windows, like its controls overlay, are never the app being launched.
bool is_couchshell_window(HWND window) {
    DWORD owner = 0;
    GetWindowThreadProcessId(window, &owner);
    const std::wstring path = process_path(owner);
    const std::wstring name = L"\\CouchShell.exe";
    return path.size() >= name.size() &&
           _wcsicmp(path.c_str() + path.size() - name.size(), name.c_str()) == 0;
}

// EnumWindows calls this for each window; collects the real app windows.
BOOL CALLBACK collect_app_window(HWND window, LPARAM list) {
    if (IsWindowVisible(window) && GetWindow(window, GW_OWNER) == nullptr &&
        GetWindowTextLengthW(window) > 0 && !is_couchshell_window(window)) {
        reinterpret_cast<std::vector<void*>*>(list)->push_back(window);
    }
    return TRUE;
}

std::vector<void*> app_windows() {
    std::vector<void*> windows;
    EnumWindows(collect_app_window, reinterpret_cast<LPARAM>(&windows));
    return windows;
}

// True if any running program lives in `folder`.
bool any_process_in_folder(const std::wstring& folder) {
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return false;
    UniqueHandle snapshot_owner(snapshot);
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (!Process32FirstW(snapshot, &entry)) return false;
    do {
        if (process_is_in_folder(entry.th32ProcessID, folder)) return true;
    } while (Process32NextW(snapshot, &entry));
    return false;
}

// EnumWindows calls this for each top-level window; `folder` is the folder to close windows from.
BOOL CALLBACK close_windows_in_folder(HWND window, LPARAM folder) {
    DWORD owner = 0;
    GetWindowThreadProcessId(window, &owner);
    if (IsWindowVisible(window) &&
        process_is_in_folder(owner, *reinterpret_cast<const std::wstring*>(folder))) {
        PostMessageW(window, WM_CLOSE, 0, 0);
    }
    return TRUE;
}

// EnumWindows calls this for every top-level window; `target` is the process id to close.
BOOL CALLBACK close_windows_of(HWND window, LPARAM target) {
    DWORD owner = 0;
    GetWindowThreadProcessId(window, &owner);
    if (owner == static_cast<DWORD>(target) && IsWindowVisible(window)) {
        PostMessageW(window, WM_CLOSE, 0, 0);  // the same message the X button sends
    }
    return TRUE;  // keep going: an app can have several windows
}

// Restarting and shutting down need this privilege switched on for our process first.
bool enable_shutdown_privilege() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token)) {
        return false;
    }
    UniqueHandle token_owner(token);

    TOKEN_PRIVILEGES privileges{};
    privileges.PrivilegeCount = 1;
    privileges.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
    if (!LookupPrivilegeValueW(nullptr, SE_SHUTDOWN_NAME, &privileges.Privileges[0].Luid)) {
        return false;
    }
    // Returns true even if the privilege wasn't granted; GetLastError() tells them apart.
    if (!AdjustTokenPrivileges(token, FALSE, &privileges, 0, nullptr, nullptr)) return false;
    return GetLastError() == ERROR_SUCCESS;
}

}  // namespace

void UniqueHandle::reset(void* handle) {
    if (handle_ != nullptr) CloseHandle(handle_);
    handle_ = handle;
}

std::int64_t WindowsPlatform::now_ms() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
               std::chrono::system_clock::now().time_since_epoch())
        .count();
}

bool WindowsPlatform::launch(const AppSpec& app, const std::string& browser, std::string& error) {
    process_.reset();
    process_name_ = to_wide(app.process_name);
    steam_app_id_.clear();
    install_folder_ = to_wide(app.install_folder);
    if (!install_folder_.empty() && install_folder_.back() != L'\\') install_folder_ += L'\\';
    watch_windows_ = app.watch_windows;
    looking_for_window_ = false;
    adopted_window_ = nullptr;

    if (app.type == AppType::kExe) {
        const std::wstring path = expand_environment(to_wide(app.target));
        if (!file_exists(path)) {
            error = "The configured executable could not be found.";
            return false;
        }
        std::wstring command_line = L"\"" + path + L"\"";
        if (!app.args.empty()) command_line += L" " + expand_environment(to_wide(app.args));
        void* process = start_process(path, command_line, error);
        if (process == nullptr) return false;
        // With an install folder, this program is only a launcher for what runs in that folder, and
        // it may well exit first. So there's no point waiting on it.
        UniqueHandle started(process);
        if (install_folder_.empty()) {
            process_.reset(process);
            started.release();
        }
        return true;
    }

    if (app.type == AppType::kWeb) {
        const std::wstring path = find_browser(browser);
        if (path.empty()) {
            error = "Neither Edge nor Chrome could be found.";
            return false;
        }
        // A dedicated profile gives the site its own browser process, instead of handing off to
        // an open window and exiting. That process is what tells us when the site is closed.
        const std::wstring profile = expand_environment(L"%LOCALAPPDATA%\\CouchShell\\browser");
        std::wstring command_line =
            L"\"" + path + L"\" --user-data-dir=\"" + profile + L"\" --no-first-run";
        if (app.full_browser) {
            command_line += L" --start-maximized " + to_wide(app.target);
        } else {
            command_line += L" --start-fullscreen --app=" + to_wide(app.target);
        }
        if (!app.user_agent.empty()) {
            command_line += L" --user-agent=\"" + to_wide(app.user_agent) + L"\"";
        }
        void* process = start_process(path, command_line, error);
        if (process == nullptr) return false;
        process_.reset(process);
        return true;
    }

    if (app.type == AppType::kSteam && process_name_.empty()) steam_app_id_ = to_wide(app.target);
    if (app.type == AppType::kShell) {
        // Taken before launching, so whatever is new afterwards must be the app.
        windows_at_launch_ = app_windows();
        front_at_launch_ = GetForegroundWindow();
        looking_for_window_ = true;
    }

    // Steam and Epic: hand the address to Windows. The launcher starts the game itself, so there's
    // no handle yet; poll() finds it another way.
    const std::wstring url = app.type == AppType::kSteam
                                 ? L"steam://rungameid/" + to_wide(app.target)
                                 : to_wide(app.target);
    SHELLEXECUTEINFOW info{};
    info.cbSize = sizeof(info);
    info.fMask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
    info.lpVerb = L"open";
    info.lpFile = url.c_str();
    info.nShow = SW_SHOWNORMAL;
    // Returns false on failure, typically when Steam isn't installed.
    if (!ShellExecuteExW(&info)) {
        error = app.type == AppType::kSteam ? "Steam could not be started."
                                            : "Its launcher could not be started.";
        return false;
    }
    return true;
}

ProcessState WindowsPlatform::poll() {
    if (process_) {
        // A zero timeout just checks: WAIT_TIMEOUT means it's still running.
        if (WaitForSingleObject(process_.get(), 0) == WAIT_TIMEOUT) return ProcessState::kRunning;
        process_.reset();
        if (process_name_.empty()) return ProcessState::kExited;
    }
    if (looking_for_window_) {
        const std::vector<void*> windows = app_windows();
        for (void* window : windows) {
            // A window that wasn't there before is the app, starting fresh.
            if (std::find(windows_at_launch_.begin(), windows_at_launch_.end(), window) ==
                windows_at_launch_.end()) {
                adopt_window(window);
                return ProcessState::kRunning;
            }
        }
        // The app was already open, so it just came to the front instead.
        HWND front = GetForegroundWindow();
        if (front != front_at_launch_ &&
            std::find(windows.begin(), windows.end(), front) != windows.end()) {
            adopt_window(front);
            return ProcessState::kRunning;
        }
        return ProcessState::kNotFound;
    }
    if (adopted_window_ != nullptr) {
        HWND window = static_cast<HWND>(adopted_window_);
        return IsWindow(window) && IsWindowVisible(window) ? ProcessState::kRunning
                                                           : ProcessState::kNotFound;
    }
    if (!steam_app_id_.empty()) {
        return steam_says_running(steam_app_id_) ? ProcessState::kRunning : ProcessState::kNotFound;
    }
    if (!install_folder_.empty()) {
        bool running;
        if (watch_windows_) {
            WindowSearch search;
            search.folder = install_folder_;
            EnumWindows(check_window, reinterpret_cast<LPARAM>(&search));
            running = search.found != nullptr;
        } else {
            running = any_process_in_folder(install_folder_);
        }
        return running ? ProcessState::kRunning : ProcessState::kNotFound;
    }
    if (process_name_.empty()) return ProcessState::kExited;
    process_.reset(open_process_by_name(process_name_));
    return process_ ? ProcessState::kRunning : ProcessState::kNotFound;
}

void WindowsPlatform::adopt_window(void* window) {
    looking_for_window_ = false;
    DWORD owner = 0;
    GetWindowThreadProcessId(static_cast<HWND>(window), &owner);
    const std::wstring path = process_path(owner);
    const std::wstring windows_folder = expand_environment(L"%SystemRoot%\\");
    const std::size_t slash = path.find_last_of(L'\\');

    // A program's folder identifies all its windows, even if a splash screen hands over to a main
    // window. Not for programs in the Windows folder: those host many unrelated windows, so there
    // only this exact window counts.
    if (slash == std::wstring::npos ||
        _wcsnicmp(path.c_str(), windows_folder.c_str(), windows_folder.size()) == 0) {
        adopted_window_ = window;
    } else {
        install_folder_ = path.substr(0, slash + 1);
        watch_windows_ = true;
    }
}

void WindowsPlatform::forget() {
    looking_for_window_ = false;
    adopted_window_ = nullptr;
    process_.reset();
    process_name_.clear();
    steam_app_id_.clear();
    install_folder_.clear();
    watch_windows_ = false;
}

void WindowsPlatform::bring_app_to_front() {
    if (adopted_window_ != nullptr) {
        take_foreground(static_cast<HWND>(adopted_window_));
        return;
    }
    WindowSearch search;
    if (process_) search.process_id = GetProcessId(process_.get());
    search.folder = install_folder_;
    if (search.process_id == 0 && search.folder.empty()) return;

    // A window of the app is already in front, e.g. a launcher or a second window: leave it.
    HWND front = GetForegroundWindow();
    if (front != nullptr) {
        DWORD owner = 0;
        GetWindowThreadProcessId(front, &owner);
        if (owner == search.process_id ||
            (!search.folder.empty() && process_is_in_folder(owner, search.folder))) {
            return;
        }
    }

    EnumWindows(check_window, reinterpret_cast<LPARAM>(&search));
    if (search.found != nullptr) take_foreground(search.found);
}

bool WindowsPlatform::close_app() {
    if (adopted_window_ != nullptr) {
        return PostMessageW(static_cast<HWND>(adopted_window_), WM_CLOSE, 0, 0) != FALSE;
    }
    if (!process_ && !install_folder_.empty()) {
        return EnumWindows(close_windows_in_folder, reinterpret_cast<LPARAM>(&install_folder_));
    }
    if (!process_) return false;
    DWORD id = GetProcessId(process_.get());
    return id != 0 && EnumWindows(close_windows_of, static_cast<LPARAM>(id));
}

bool WindowsPlatform::get_volume(int& percent, bool& muted) {
    ComPtr<IAudioEndpointVolume> endpoint;
    if (!audio_endpoint(endpoint)) return false;
    float level = 0.0f;
    BOOL is_muted = FALSE;
    if (FAILED(endpoint->GetMasterVolumeLevelScalar(&level))) return false;
    if (FAILED(endpoint->GetMute(&is_muted))) return false;
    percent = static_cast<int>(level * 100.0f + 0.5f);
    muted = is_muted != FALSE;
    return true;
}

bool WindowsPlatform::set_volume(int percent) {
    ComPtr<IAudioEndpointVolume> endpoint;
    if (!audio_endpoint(endpoint)) return false;
    return SUCCEEDED(
        endpoint->SetMasterVolumeLevelScalar(static_cast<float>(percent) / 100.0f, nullptr));
}

bool WindowsPlatform::set_muted(bool muted) {
    ComPtr<IAudioEndpointVolume> endpoint;
    if (!audio_endpoint(endpoint)) return false;
    return SUCCEEDED(endpoint->SetMute(muted ? TRUE : FALSE, nullptr));
}

bool WindowsPlatform::power(PowerAction action, std::string& error) {
    if (dry_run_power_) {
        std::cout << "couchd: dry run, not performing power action" << std::endl;
        return true;
    }
    if (action == PowerAction::kSleep) {
        // Arguments: not hibernate, not forced, wake events stay enabled.
        if (!SetSuspendState(FALSE, FALSE, FALSE)) {
            error = "Windows could not go to sleep.";
            return false;
        }
        return true;
    }
    if (!enable_shutdown_privilege()) {
        error = "CouchShell doesn't have permission to do that.";
        return false;
    }
    // Asks apps to close normally, and doesn't force them. Returns before the shutdown happens.
    UINT flags = action == PowerAction::kRestart ? EWX_REBOOT : EWX_POWEROFF;
    if (!ExitWindowsEx(flags, SHTDN_REASON_MAJOR_OTHER | SHTDN_REASON_FLAG_PLANNED)) {
        error = "Windows refused the request.";
        return false;
    }
    return true;
}

}  // namespace couchd
