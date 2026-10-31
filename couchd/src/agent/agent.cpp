#include "agent/agent.h"

#include <algorithm>
#include <cstddef>
#include <iostream>
#include <sstream>

namespace couchd {

namespace {

// The keys couchd owns in Tachyon.
const char* const kRunningKey = "couch:running";    // id of the running app; missing when idle
const char* const kAliveKey = "couch:alive";        // heartbeat, expires if couchd dies
const char* const kVolumeKey = "couch:volume";      // hash: percent, muted
const char* const kRecentKey = "couch:recent";      // list of app ids, newest first
const char* const kSessionsKey = "couch:sessions";  // list of "id\tstart_ms\tend_ms", newest first
const char* const kSettingsKey = "couch:settings";  // hash: browser, ...

const char* const kRecentLimit = "19";    // LTRIM's last index: keeps 20 entries
const char* const kSessionLimit = "199";  // keeps 200 entries

const std::int64_t kPollIntervalMs = 500;
const std::int64_t kHeartbeatIntervalMs = 2000;
const char* const kAliveLifetimeMs = "5000";
const std::int64_t kVolumeIntervalMs = 1000;
// A Steam game can take a long time to appear after the launch.
const std::int64_t kStartTimeoutMs = 120000;
// Launchers and games sometimes swap processes, so a name must stay missing this long to count
// as an exit.
const std::int64_t kExitGraceMs = 5000;
// Games take a while to open their window, which can appear behind other windows, and controllers
// only reach the window in front. Long enough for a slow start, short enough not to fight someone
// who later switches away on purpose.
const std::int64_t kKeepInFrontMs = 45000;
const int kVolumeStep = 5;

// Only characters that can't break a space-separated request or a key name.
bool valid_id(const std::string& id) {
    if (id.empty() || id.size() > 64) return false;
    return std::all_of(id.begin(), id.end(), [](char c) {
        return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
    });
}

// The value is passed to a browser on its command line, so nothing may end the argument early.
bool valid_url(const std::string& url) {
    if (url.rfind("http://", 0) != 0 && url.rfind("https://", 0) != 0) return false;
    return std::none_of(url.begin(), url.end(), [](char c) {
        return c == '"' || c == ' ' || c == '\t' || c == '\r' || c == '\n';
    });
}

bool all_digits(const std::string& text) {
    return !text.empty() &&
           std::all_of(text.begin(), text.end(), [](char c) { return c >= '0' && c <= '9'; });
}

std::string field(const std::map<std::string, std::string>& fields, const std::string& name) {
    std::map<std::string, std::string>::const_iterator found = fields.find(name);
    return found == fields.end() ? "" : found->second;
}

std::vector<std::string> split_words(const std::string& text) {
    std::vector<std::string> words;
    std::istringstream stream(text);
    std::string word;
    while (stream >> word) {
        words.push_back(word);
    }
    return words;
}

}  // namespace

bool parse_app(const std::string& id, const std::map<std::string, std::string>& fields,
               AppSpec& app, std::string& error) {
    app.id = id;
    app.target = field(fields, "target");
    app.args = field(fields, "args");
    app.process_name = field(fields, "processName");
    app.user_agent = field(fields, "userAgent");
    app.full_browser = field(fields, "window") == "browser";
    app.install_folder = field(fields, "installFolder");
    app.watch_windows = field(fields, "watch") == "window";
    const std::string type = field(fields, "type");

    if (type.empty() || app.target.empty()) {
        error = "This app is not configured.";
        return false;
    }
    if (type == "exe") {
        app.type = AppType::kExe;
        if (app.target.find('"') != std::string::npos) {
            error = "The configured executable path is not valid.";
            return false;
        }
    } else if (type == "web") {
        app.type = AppType::kWeb;
        if (!valid_url(app.target)) {
            error = "The configured web address is not valid.";
            return false;
        }
        // Goes inside quotes on the browser's command line, so a quote could end the argument.
        if (app.user_agent.find('"') != std::string::npos) {
            error = "The configured user agent is not valid.";
            return false;
        }
    } else if (type == "steam") {
        app.type = AppType::kSteam;
        if (!all_digits(app.target)) {
            error = "The configured Steam app id is not valid.";
            return false;
        }
    } else if (type == "shell") {
        app.type = AppType::kShell;
        // An entry from Windows' All apps list. Names can contain spaces, but nothing else odd.
        if (app.target.rfind("shell:AppsFolder\\", 0) != 0 ||
            app.target.find_first_of("\"\r\n") != std::string::npos) {
            error = "This app's address is not valid.";
            return false;
        }
    } else if (type == "uri") {
        app.type = AppType::kUri;
        // Windows hands these to whatever program registered the scheme, so only known ones pass.
        if (app.target.rfind("com.epicgames.launcher://", 0) != 0 ||
            app.target.find_first_of("\" \t\r\n") != std::string::npos) {
            error = "The configured launch address is not valid.";
            return false;
        }
    } else {
        error = "This app has an unknown type.";
        return false;
    }
    return true;
}

void Agent::on_connected() {
    if (state_ == State::kIdle) {
        // Left over from a couchd that died with an app running: nothing is being watched now.
        enqueue({"DEL", kRunningKey});
    } else if (state_ == State::kRunning) {
        enqueue({"SET", kRunningKey, app_id_});
    }
    next_heartbeat_ = 0;
    volume_known_ = false;  // publish the current volume again
    flush();
}

void Agent::handle_request(const std::string& request) {
    std::vector<std::string> words = split_words(request);
    if (words.empty()) return;

    if (words[0] == "launch" && words.size() == 2) {
        handle_launch(words[1]);
    } else if (words[0] == "leave" && words.size() == 1) {
        // Stop tracking it but leave it running, like music playing in the background. To the
        // rest of CouchShell this looks like the app ending, so it comes back as usual.
        if (state_ == State::kRunning) mark_exited();
    } else if (words[0] == "close" && words.size() == 1) {
        if (state_ == State::kStarting) {
            cancel_launch();
        } else if (state_ == State::kRunning && !platform_.close_app()) {
            std::cout << "couchd: could not close the running app" << std::endl;
        }
    } else if (words[0] == "volume") {
        handle_volume(words);
    } else if (words[0] == "power" && words.size() == 2) {
        handle_power(words[1]);
    } else {
        std::cout << "couchd: ignoring unknown request '" << request << "'" << std::endl;
    }
}

void Agent::fail_launch(const std::string& id, const std::string& reason) {
    std::cout << "couchd: launch of '" << id << "' failed: " << reason << std::endl;
    publish("launch_failed " + id + " " + reason);
}

void Agent::handle_launch(const std::string& id) {
    if (!valid_id(id)) {
        std::cout << "couchd: ignoring launch of an invalid app id" << std::endl;
        return;
    }
    if (state_ == State::kRunning) {
        fail_launch(id, "Another app is already running.");
        return;
    }
    // Still waiting on an earlier launch, e.g. someone closed Steam's launch options dialog,
    // which nothing reports. A new choice replaces it rather than being refused.
    if (state_ == State::kStarting) cancel_launch();

    Reply hash;
    if (!bus_.run({"HGETALL", "app:" + id}, hash) || hash.kind != Reply::Kind::kArray) {
        fail_launch(id, "Its settings could not be read.");
        return;
    }
    AppSpec app;
    std::string error;
    if (!parse_app(id, hash.to_map(), app, error)) {
        fail_launch(id, error);
        return;
    }

    // Best effort: without a saved preference, the platform picks whichever browser it finds.
    Reply browser;
    bus_.run({"HGET", kSettingsKey, "browser"}, browser);

    if (!platform_.launch(app, browser.text, error)) {
        fail_launch(id, error);
        return;
    }
    app_id_ = id;
    started_at_ = platform_.now_ms();
    not_found_since_ = 0;
    state_ = State::kStarting;
    next_poll_ = 0;  // look straight away, so a normal launch reports "started" at once
    poll_app(started_at_);
    // Not running yet, e.g. Steam is updating or showing a dialog. The UI gets out of the way, so
    // that dialog can be used.
    // A shell app is the exception: its window is found by watching what appears, and CouchShell
    // staying in front until then makes that reliable.
    if (state_ == State::kStarting && app.type != AppType::kShell) publish("launching " + id);
}

void Agent::cancel_launch() {
    const std::string id = app_id_;
    platform_.forget();
    state_ = State::kIdle;
    app_id_.clear();
    publish("launch_cancelled " + id);
}

void Agent::handle_volume(const std::vector<std::string>& words) {
    int percent = 0;
    bool muted = false;
    if (!platform_.get_volume(percent, muted)) {
        std::cout << "couchd: volume control is not available" << std::endl;
        return;
    }
    if (words.size() == 3 && words[1] == "set" && all_digits(words[2]) && words[2].size() <= 3) {
        platform_.set_volume(std::min(std::stoi(words[2]), 100));
    } else if (words.size() == 2 && words[1] == "up") {
        platform_.set_volume(std::min(percent + kVolumeStep, 100));
    } else if (words.size() == 2 && words[1] == "down") {
        platform_.set_volume(std::max(percent - kVolumeStep, 0));
    } else if (words.size() == 2 && words[1] == "mute") {
        platform_.set_muted(!muted);
    } else {
        std::cout << "couchd: ignoring unknown volume request" << std::endl;
        return;
    }
    check_volume();
}

void Agent::handle_power(const std::string& action) {
    PowerAction power_action;
    if (action == "sleep") {
        power_action = PowerAction::kSleep;
    } else if (action == "restart") {
        power_action = PowerAction::kRestart;
    } else if (action == "shutdown") {
        power_action = PowerAction::kShutdown;
    } else {
        std::cout << "couchd: ignoring unknown power request" << std::endl;
        return;
    }
    std::string error;
    if (!platform_.power(power_action, error)) {
        std::cout << "couchd: power request failed: " << error << std::endl;
        publish("power_failed " + error);
    }
}

void Agent::mark_started() {
    state_ = State::kRunning;
    keep_in_front_until_ = platform_.now_ms() + kKeepInFrontMs;
    enqueue({"SET", kRunningKey, app_id_});
    enqueue({"LPUSH", kRecentKey, app_id_});
    enqueue({"LTRIM", kRecentKey, "0", kRecentLimit});
    publish("started " + app_id_);
}

void Agent::mark_exited() {
    const std::string session =
        app_id_ + "\t" + std::to_string(started_at_) + "\t" + std::to_string(platform_.now_ms());
    enqueue({"DEL", kRunningKey});
    enqueue({"LPUSH", kSessionsKey, session});
    enqueue({"LTRIM", kSessionsKey, "0", kSessionLimit});
    publish("exited " + app_id_);
    platform_.forget();
    state_ = State::kIdle;
    app_id_.clear();
}

void Agent::poll_app(std::int64_t now) {
    ProcessState process = platform_.poll();
    if (process != ProcessState::kNotFound) {
        not_found_since_ = 0;
    }

    if (state_ == State::kStarting) {
        if (process == ProcessState::kRunning) {
            mark_started();
        } else if (process == ProcessState::kExited) {
            // It exited immediately. Report both, so the UI goes through its normal path.
            mark_started();
            mark_exited();
        } else if (now - started_at_ > kStartTimeoutMs) {
            const std::string id = app_id_;
            platform_.forget();
            state_ = State::kIdle;
            app_id_.clear();
            fail_launch(id, "It did not start in time.");
        }
        return;
    }

    if (process == ProcessState::kRunning && now < keep_in_front_until_) {
        platform_.bring_app_to_front();
    }

    if (process == ProcessState::kExited) {
        mark_exited();
    } else if (process == ProcessState::kNotFound) {
        if (not_found_since_ == 0) {
            not_found_since_ = now;
        } else if (now - not_found_since_ >= kExitGraceMs) {
            mark_exited();
        }
    }
}

void Agent::check_volume() {
    int percent = 0;
    bool muted = false;
    if (!platform_.get_volume(percent, muted)) return;  // e.g. no audio device right now
    if (volume_known_ && percent == volume_ && muted == muted_) return;

    volume_known_ = true;
    volume_ = percent;
    muted_ = muted;
    const std::string percent_text = std::to_string(percent);
    const std::string muted_text = muted ? "1" : "0";
    enqueue({"HSET", kVolumeKey, "percent", percent_text, "muted", muted_text});
    publish("volume " + percent_text + " " + muted_text);
}

void Agent::tick() {
    const std::int64_t now = platform_.now_ms();

    if (state_ != State::kIdle && now >= next_poll_) {
        next_poll_ = now + kPollIntervalMs;
        poll_app(now);
    }
    // Skipped while writes are waiting: there's no point queueing more while Tachyon is away.
    if (pending_.empty() && now >= next_volume_check_) {
        next_volume_check_ = now + kVolumeIntervalMs;
        check_volume();
    }
    if (now >= next_heartbeat_) {
        Reply reply;
        if (bus_.run({"SET", kAliveKey, "1", "PX", kAliveLifetimeMs}, reply)) {
            next_heartbeat_ = now + kHeartbeatIntervalMs;
        }
    }
    flush();
}

void Agent::enqueue(std::vector<std::string> command) { pending_.push_back(command); }

void Agent::publish(const std::string& event) {
    enqueue({"PUBLISH", kEventChannel, event});
    flush();
}

void Agent::flush() {
    while (!pending_.empty()) {
        Reply reply;
        if (!bus_.run(pending_.front(), reply)) return;  // try again on a later tick
        if (reply.kind == Reply::Kind::kError) {
            std::cout << "couchd: Tachyon rejected a write: " << reply.text << std::endl;
        }
        pending_.pop_front();
    }
}

}  // namespace couchd
