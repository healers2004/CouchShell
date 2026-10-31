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
    const std::vector<std::wstring>& first = preferred == "chrome" ? chrome : edge;
    const std::vector<std::wstring>& second = preferred == "chrome" ? edge : chrome;
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
        process_.reset(process);
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
        std::wstring command_line = L"\"" + path + L"\" --app=" + to_wide(app.target) +
                                    L" --user-data-dir=\"" + profile +
                                    L"\" --start-fullscreen --no-first-run";
        void* process = start_process(path, command_line, error);
        if (process == nullptr) return false;
        process_.reset(process);
        return true;
    }

    // Steam: hand the URL to Windows. Steam starts the game itself, so there's no handle yet;
    // poll() finds it by name.
    const std::wstring url = L"steam://rungameid/" + to_wide(app.target);
    SHELLEXECUTEINFOW info{};
    info.cbSize = sizeof(info);
    info.fMask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
    info.lpVerb = L"open";
    info.lpFile = url.c_str();
    info.nShow = SW_SHOWNORMAL;
    // Returns false on failure, typically when Steam isn't installed.
    if (!ShellExecuteExW(&info)) {
        error = "Steam could not be started.";
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
    if (process_name_.empty()) return ProcessState::kExited;
    process_.reset(open_process_by_name(process_name_));
    return process_ ? ProcessState::kRunning : ProcessState::kNotFound;
}

void WindowsPlatform::forget() {
    process_.reset();
    process_name_.clear();
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
