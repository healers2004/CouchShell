// couchd's logic, with no OS or network calls: it reads requests, tracks the running app and
// reports what happens. The real Windows and socket code plugs in through Platform and Bus, and
// the tests plug in fakes.
#pragma once

#include <cstdint>
#include <deque>
#include <map>
#include <string>
#include <vector>

#include "protocol/reply_parser.h"

namespace couchd {

// Requests arrive here as text, e.g. "launch youtube", "volume set 40", "power sleep".
const char* const kRequestChannel = "couch:requests";
// Events go out here, e.g. "started youtube", "exited youtube", "volume 40 0".
const char* const kEventChannel = "couch:events";

enum class AppType { kExe, kWeb, kSteam };

struct AppSpec {
    std::string id;
    AppType type = AppType::kExe;
    std::string target;        // executable path, URL or Steam app id
    std::string args;          // extra command line, for executables
    std::string process_name;  // the process to watch, when it isn't the one started directly
};

// Builds an AppSpec from an `app:<id>` hash. Returns false and sets a user-facing `error` if a
// field is missing or unsafe, e.g. a URL that could smuggle extra browser arguments.
bool parse_app(const std::string& id, const std::map<std::string, std::string>& fields,
               AppSpec& app, std::string& error);

enum class ProcessState {
    kRunning,
    kExited,    // the process started directly has gone, and there's no other process to look for
    kNotFound,  // watching by name, and no process has that name right now
};

enum class PowerAction { kSleep, kRestart, kShutdown };

// Everything couchd needs from the operating system. `virtual`: each implementation supplies its
// own version, and calls through a Platform& reach it.
class Platform {
public:
    virtual ~Platform() = default;
    virtual std::int64_t now_ms() = 0;  // Unix time in milliseconds

    // Starts the app. Returns false and sets a user-facing `error` on failure.
    virtual bool launch(const AppSpec& app, const std::string& browser, std::string& error) = 0;
    // The state of the app started by the last successful launch().
    virtual ProcessState poll() = 0;
    // Stops watching that app; it keeps running if it's still alive.
    virtual void forget() = 0;

    virtual bool get_volume(int& percent, bool& muted) = 0;
    virtual bool set_volume(int percent) = 0;
    virtual bool set_muted(bool muted) = 0;
    virtual bool power(PowerAction action, std::string& error) = 0;
};

// The connection to Tachyon.
class Bus {
public:
    virtual ~Bus() = default;
    // Runs one command. Returns false if it couldn't reach Tachyon.
    virtual bool run(const std::vector<std::string>& args, Reply& reply) = 0;
};

class Agent {
public:
    Agent(Platform& platform, Bus& bus) : platform_(platform), bus_(bus) {}

    // Call after every (re)connection: restores the keys Tachyon may have lost.
    void on_connected();
    // Call for every message on kRequestChannel.
    void handle_request(const std::string& request);
    // Call often, whether or not Tachyon is reachable.
    void tick();

private:
    enum class State { kIdle, kStarting, kRunning };

    void handle_launch(const std::string& id);
    void handle_volume(const std::vector<std::string>& words);
    void handle_power(const std::string& action);
    void fail_launch(const std::string& id, const std::string& reason);
    void mark_started();
    void mark_exited();
    void poll_app(std::int64_t now);
    void check_volume();

    // Writes wait here until Tachyon accepts them, so an app that exits while Tachyon is
    // restarting still gets its "exited" event and its history entry.
    void enqueue(std::vector<std::string> command);
    void publish(const std::string& event);
    void flush();

    Platform& platform_;
    Bus& bus_;
    std::deque<std::vector<std::string>> pending_;

    State state_ = State::kIdle;
    std::string app_id_;
    std::int64_t started_at_ = 0;       // when the launch was requested
    std::int64_t not_found_since_ = 0;  // 0 while the watched process is present

    std::int64_t next_poll_ = 0;
    std::int64_t next_heartbeat_ = 0;
    std::int64_t next_volume_check_ = 0;

    bool volume_known_ = false;
    int volume_ = 0;
    bool muted_ = false;
};

}  // namespace couchd
