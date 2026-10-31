// The real Platform: starts and watches processes, and controls audio and power, through Win32.
#pragma once

#include <string>

#include "agent/agent.h"

namespace couchd {

// Owns a Windows HANDLE and closes it when destroyed, so no path can leak one. void* is what
// HANDLE really is, which keeps <windows.h> out of this header.
class UniqueHandle {
public:
    UniqueHandle() = default;
    explicit UniqueHandle(void* handle) : handle_(handle) {}
    ~UniqueHandle() { reset(); }

    // Copying would close the same handle twice.
    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;

    void* get() const { return handle_; }
    explicit operator bool() const { return handle_ != nullptr; }
    // Closes the current handle and takes ownership of `handle`.
    void reset(void* handle = nullptr);

private:
    void* handle_ = nullptr;
};

class WindowsPlatform : public Platform {
public:
    // With dry_run_power, power requests are only logged. The integration test relies on this.
    explicit WindowsPlatform(bool dry_run_power) : dry_run_power_(dry_run_power) {}

    std::int64_t now_ms() override;
    bool launch(const AppSpec& app, const std::string& browser, std::string& error) override;
    ProcessState poll() override;
    void forget() override;
    bool get_volume(int& percent, bool& muted) override;
    bool set_volume(int percent) override;
    bool set_muted(bool muted) override;
    bool power(PowerAction action, std::string& error) override;

private:
    bool dry_run_power_;
    UniqueHandle process_;       // the process being waited on, if known
    std::wstring process_name_;  // look for this process when there's no handle (may be empty)
};

}  // namespace couchd
