#include "agent/agent.h"

#include <gtest/gtest.h>

#include <algorithm>
#include <map>
#include <string>
#include <vector>

using couchd::Agent;
using couchd::AppSpec;
using couchd::AppType;
using couchd::Bus;
using couchd::Platform;
using couchd::PowerAction;
using couchd::ProcessState;
using couchd::Reply;

namespace {

class FakePlatform : public Platform {
public:
    std::int64_t now = 1000;
    bool launch_ok = true;
    std::string launch_error = "The configured executable could not be found.";
    ProcessState process = ProcessState::kRunning;
    std::vector<AppSpec> launched;
    std::string browser_seen;
    bool has_audio = true;
    int percent = 50;
    bool muted = false;
    bool power_ok = true;
    std::vector<PowerAction> power_calls;
    int forget_calls = 0;
    int close_calls = 0;
    int front_calls = 0;

    std::int64_t now_ms() override { return now; }
    bool launch(const AppSpec& app, const std::string& browser, std::string& error) override {
        browser_seen = browser;
        if (!launch_ok) {
            error = launch_error;
            return false;
        }
        launched.push_back(app);
        return true;
    }
    ProcessState poll() override { return process; }
    void forget() override { ++forget_calls; }
    bool close_app() override {
        ++close_calls;
        return true;
    }
    void bring_app_to_front() override { ++front_calls; }
    bool get_volume(int& out_percent, bool& out_muted) override {
        out_percent = percent;
        out_muted = muted;
        return has_audio;
    }
    bool set_volume(int value) override {
        percent = value;
        return true;
    }
    bool set_muted(bool value) override {
        muted = value;
        return true;
    }
    bool power(PowerAction action, std::string& error) override {
        power_calls.push_back(action);
        if (!power_ok) error = "Windows refused.";
        return power_ok;
    }
};

// Records every command and answers HGETALL/HGET from `hashes`.
class FakeBus : public Bus {
public:
    bool connected = true;
    std::vector<std::vector<std::string>> commands;
    std::map<std::string, std::map<std::string, std::string>> hashes;

    bool run(const std::vector<std::string>& args, Reply& reply) override {
        if (!connected) return false;
        commands.push_back(args);
        reply = Reply{};
        if (args[0] == "HGETALL") {
            reply.kind = Reply::Kind::kArray;
            for (const auto& entry : hashes[args[1]]) {
                Reply name;
                name.kind = Reply::Kind::kBulk;
                name.text = entry.first;
                Reply value;
                value.kind = Reply::Kind::kBulk;
                value.text = entry.second;
                reply.items.push_back(name);
                reply.items.push_back(value);
            }
        } else if (args[0] == "HGET" && hashes[args[1]].count(args[2]) > 0) {
            reply.kind = Reply::Kind::kBulk;
            reply.text = hashes[args[1]][args[2]];
        }
        return true;
    }

    std::vector<std::string> events() const {
        std::vector<std::string> result;
        for (const std::vector<std::string>& command : commands) {
            if (command[0] == "PUBLISH") result.push_back(command[2]);
        }
        return result;
    }

    bool sent(const std::vector<std::string>& command) const {
        return std::find(commands.begin(), commands.end(), command) != commands.end();
    }
};

class AgentTest : public ::testing::Test {
protected:
    AgentTest() : agent(platform, bus) {
        platform.has_audio = false;  // most tests aren't about volume
        bus.hashes["app:notes"] = {{"type", "exe"}, {"target", "C:\\notes.exe"}};
    }

    void advance(std::int64_t ms) {
        platform.now += ms;
        agent.tick();
    }

    FakePlatform platform;
    FakeBus bus;
    Agent agent;
};

using Events = std::vector<std::string>;

TEST_F(AgentTest, LaunchThenExitReportsAndRecordsTheSession) {
    agent.handle_request("launch notes");
    ASSERT_EQ(platform.launched.size(), 1u);
    EXPECT_EQ(platform.launched[0].target, "C:\\notes.exe");
    EXPECT_EQ(bus.events(), (Events{"started notes"}));
    EXPECT_TRUE(bus.sent({"SET", "couch:running", "notes"}));
    EXPECT_TRUE(bus.sent({"LPUSH", "couch:recent", "notes"}));

    platform.process = ProcessState::kExited;
    advance(60000);
    EXPECT_EQ(bus.events(), (Events{"started notes", "exited notes"}));
    EXPECT_TRUE(bus.sent({"DEL", "couch:running"}));
    EXPECT_TRUE(bus.sent({"LPUSH", "couch:sessions", "notes\t1000\t61000"}));
    EXPECT_EQ(platform.forget_calls, 1);
}

TEST_F(AgentTest, AppThatExitsImmediatelyStillGoesThroughStartedAndExited) {
    platform.process = ProcessState::kExited;
    agent.handle_request("launch notes");
    EXPECT_EQ(bus.events(), (Events{"started notes", "exited notes"}));
}

TEST_F(AgentTest, LaunchFailureIsReportedAndLeavesCouchdIdle) {
    platform.launch_ok = false;
    agent.handle_request("launch notes");
    EXPECT_EQ(bus.events(),
              (Events{"launch_failed notes The configured executable could not be found."}));

    platform.launch_ok = true;
    agent.handle_request("launch notes");
    EXPECT_EQ(bus.events().back(), "started notes");
}

TEST_F(AgentTest, UnconfiguredAppFails) {
    agent.handle_request("launch ghost");
    EXPECT_EQ(bus.events(), (Events{"launch_failed ghost This app is not configured."}));
    EXPECT_TRUE(platform.launched.empty());
}

TEST_F(AgentTest, SecondLaunchIsRefusedWhileOneIsRunning) {
    agent.handle_request("launch notes");
    agent.handle_request("launch notes");
    EXPECT_EQ(platform.launched.size(), 1u);
    EXPECT_EQ(bus.events().back(), "launch_failed notes Another app is already running.");
}

TEST_F(AgentTest, InvalidIdsNeverReachTachyonOrTheOs) {
    agent.handle_request("launch ../evil");
    agent.handle_request("launch UPPER");
    agent.handle_request("launch");
    agent.handle_request("launch a b");
    agent.handle_request("nonsense");
    agent.handle_request("");
    EXPECT_TRUE(bus.commands.empty());
    EXPECT_TRUE(platform.launched.empty());
}

TEST_F(AgentTest, SteamGameIsStartedOnlyOnceItsProcessAppears) {
    bus.hashes["app:game"] = {{"type", "steam"}, {"target", "440"}, {"processName", "hl2.exe"}};
    platform.process = ProcessState::kNotFound;
    agent.handle_request("launch game");
    EXPECT_EQ(bus.events(), (Events{"launching game"}));

    advance(5000);
    EXPECT_EQ(bus.events().size(), 1u);

    platform.process = ProcessState::kRunning;
    advance(5000);
    EXPECT_EQ(bus.events(), (Events{"launching game", "started game"}));
}

TEST_F(AgentTest, ProcessThatNeverAppearsGivesUpCleanly) {
    bus.hashes["app:game"] = {{"type", "steam"}, {"target", "440"}, {"processName", "hl2.exe"}};
    platform.process = ProcessState::kNotFound;
    agent.handle_request("launch game");

    advance(120001);
    EXPECT_EQ(bus.events(),
              (Events{"launching game", "launch_failed game It did not start in time."}));
    EXPECT_EQ(platform.forget_calls, 1);

    // couchd is idle again, so another launch works.
    platform.process = ProcessState::kRunning;
    agent.handle_request("launch notes");
    EXPECT_EQ(bus.events().back(), "started notes");
}

TEST_F(AgentTest, MissingProcessNameCountsAsExitOnlyAfterAGracePeriod) {
    bus.hashes["app:game"] = {{"type", "steam"}, {"target", "440"}, {"processName", "hl2.exe"}};
    agent.handle_request("launch game");

    platform.process = ProcessState::kNotFound;
    advance(1000);
    advance(4000);
    EXPECT_EQ(bus.events().back(), "started game");

    // It came back (a launcher swapping processes), so the grace period starts over.
    platform.process = ProcessState::kRunning;
    advance(1000);
    platform.process = ProcessState::kNotFound;
    advance(1000);
    advance(4000);
    EXPECT_EQ(bus.events().back(), "started game");

    advance(1000);
    EXPECT_EQ(bus.events().back(), "exited game");
}

TEST_F(AgentTest, EventsWaitForTachyonToComeBack) {
    agent.handle_request("launch notes");
    bus.connected = false;
    platform.process = ProcessState::kExited;
    advance(1000);
    EXPECT_EQ(bus.events(), (Events{"started notes"}));

    bus.connected = true;
    advance(1000);
    EXPECT_EQ(bus.events(), (Events{"started notes", "exited notes"}));
    EXPECT_TRUE(bus.sent({"DEL", "couch:running"}));
}

TEST_F(AgentTest, ReconnectRestoresState) {
    agent.on_connected();
    EXPECT_TRUE(bus.sent({"DEL", "couch:running"}));

    agent.handle_request("launch notes");
    bus.commands.clear();
    agent.on_connected();
    EXPECT_TRUE(bus.sent({"SET", "couch:running", "notes"}));
}

TEST_F(AgentTest, HeartbeatRepeatsAndExpires) {
    agent.tick();
    const std::vector<std::string> heartbeat = {"SET", "couch:alive", "1", "PX", "5000"};
    EXPECT_TRUE(bus.sent(heartbeat));

    bus.commands.clear();
    advance(1000);
    EXPECT_FALSE(bus.sent(heartbeat));
    advance(1000);
    EXPECT_TRUE(bus.sent(heartbeat));
}

TEST_F(AgentTest, PassesTheBrowserPreference) {
    bus.hashes["app:site"] = {{"type", "web"}, {"target", "https://example.com"}};
    bus.hashes["couch:settings"] = {{"browser", "chrome"}};
    agent.handle_request("launch site");
    EXPECT_EQ(platform.browser_seen, "chrome");
}

TEST_F(AgentTest, VolumeRequestsApplyClampAndPublish) {
    platform.has_audio = true;
    agent.handle_request("volume set 40");
    EXPECT_EQ(platform.percent, 40);
    EXPECT_EQ(bus.events(), (Events{"volume 40 0"}));
    EXPECT_TRUE(bus.sent({"HSET", "couch:volume", "percent", "40", "muted", "0"}));

    agent.handle_request("volume up");
    EXPECT_EQ(platform.percent, 45);
    agent.handle_request("volume mute");
    EXPECT_TRUE(platform.muted);
    EXPECT_EQ(bus.events().back(), "volume 45 1");

    agent.handle_request("volume set 999");
    EXPECT_EQ(platform.percent, 100);
    agent.handle_request("volume up");
    EXPECT_EQ(platform.percent, 100);

    platform.percent = 2;
    agent.handle_request("volume down");
    EXPECT_EQ(platform.percent, 0);

    platform.percent = 30;
    agent.handle_request("volume set abc");
    agent.handle_request("volume");
    EXPECT_EQ(platform.percent, 30);
}

TEST_F(AgentTest, VolumeChangedOutsideCouchShellIsPublishedOnce) {
    platform.has_audio = true;
    agent.tick();
    EXPECT_EQ(bus.events(), (Events{"volume 50 0"}));

    advance(1000);
    EXPECT_EQ(bus.events().size(), 1u);

    platform.percent = 70;
    advance(1000);
    EXPECT_EQ(bus.events().back(), "volume 70 0");
}

TEST_F(AgentTest, CloseOnlyActsWhileAnAppIsRunning) {
    agent.handle_request("close");
    EXPECT_EQ(platform.close_calls, 0);

    agent.handle_request("launch notes");
    agent.handle_request("close");
    EXPECT_EQ(platform.close_calls, 1);
}

TEST_F(AgentTest, SteamWaitingOnADialogAnnouncesItselfAndCanBeCancelled) {
    bus.hashes["app:game"] = {{"type", "steam"}, {"target", "440"}};
    platform.process = ProcessState::kNotFound;
    agent.handle_request("launch game");
    EXPECT_EQ(bus.events(), (Events{"launching game"}));

    agent.handle_request("close");
    EXPECT_EQ(bus.events().back(), "launch_cancelled game");
    EXPECT_EQ(platform.forget_calls, 1);
    EXPECT_EQ(platform.close_calls, 0);

    // Idle again, so the next launch works straight away.
    platform.process = ProcessState::kRunning;
    agent.handle_request("launch notes");
    EXPECT_EQ(bus.events().back(), "started notes");
}

TEST_F(AgentTest, ANewLaunchReplacesOneStillWaiting) {
    bus.hashes["app:game"] = {{"type", "steam"}, {"target", "440"}};
    platform.process = ProcessState::kNotFound;
    agent.handle_request("launch game");

    platform.process = ProcessState::kRunning;
    agent.handle_request("launch notes");
    EXPECT_EQ(bus.events(), (Events{"launching game", "launch_cancelled game", "started notes"}));
}

TEST_F(AgentTest, KeepsTheAppInFrontOnlyWhileItStarts) {
    agent.handle_request("launch notes");
    advance(1000);
    EXPECT_GT(platform.front_calls, 0);

    platform.now += 45000;  // past the window
    agent.tick();
    int calls_after_window = platform.front_calls;
    advance(1000);
    advance(1000);
    EXPECT_EQ(platform.front_calls, calls_after_window);
}

TEST_F(AgentTest, DoesNotBringAnythingForwardWhileWaitingOrIdle) {
    bus.hashes["app:game"] = {{"type", "steam"}, {"target", "440"}};
    platform.process = ProcessState::kNotFound;
    agent.handle_request("launch game");
    advance(1000);
    advance(1000);
    EXPECT_EQ(platform.front_calls, 0);  // Steam's dialog must keep focus

    agent.handle_request("close");
    advance(1000);
    EXPECT_EQ(platform.front_calls, 0);
}

TEST_F(AgentTest, LeavingAnAppKeepsItRunningAndFreesCouchdForTheNextOne) {
    agent.handle_request("leave");  // nothing running: nothing happens
    EXPECT_TRUE(bus.events().empty());

    agent.handle_request("launch notes");
    agent.handle_request("leave");
    EXPECT_EQ(bus.events().back(), "exited notes");
    EXPECT_EQ(platform.close_calls, 0);  // it was never asked to close
    EXPECT_EQ(platform.forget_calls, 1);
    EXPECT_TRUE(bus.sent({"DEL", "couch:running"}));

    agent.handle_request("launch notes");
    EXPECT_EQ(bus.events().back(), "started notes");
}

TEST_F(AgentTest, LeavingDoesNothingWhileStillWaitingForALaunch) {
    bus.hashes["app:game"] = {{"type", "steam"}, {"target", "440"}};
    platform.process = ProcessState::kNotFound;
    agent.handle_request("launch game");
    agent.handle_request("leave");
    EXPECT_EQ(bus.events(), (Events{"launching game"}));
}

TEST_F(AgentTest, NormalLaunchDoesNotAnnounceWaiting) {
    agent.handle_request("launch notes");
    EXPECT_EQ(bus.events(), (Events{"started notes"}));
}

TEST(ParseApp, SteamNeedsNoProcessName) {
    AppSpec app;
    std::string error;
    EXPECT_TRUE(couchd::parse_app("a", {{"type", "steam"}, {"target", "440"}}, app, error));
    EXPECT_TRUE(app.process_name.empty());
}

TEST_F(AgentTest, PowerRequests) {
    agent.handle_request("power sleep");
    agent.handle_request("power restart");
    agent.handle_request("power shutdown");
    agent.handle_request("power explode");
    EXPECT_EQ(platform.power_calls,
              (std::vector<PowerAction>{PowerAction::kSleep, PowerAction::kRestart,
                                        PowerAction::kShutdown}));
    EXPECT_TRUE(bus.events().empty());

    platform.power_ok = false;
    agent.handle_request("power sleep");
    EXPECT_EQ(bus.events(), (Events{"power_failed Windows refused."}));
}

TEST(ParseApp, AcceptsOnlyKnownLaunchAddresses) {
    AppSpec app;
    std::string error;
    EXPECT_TRUE(couchd::parse_app(
        "a", {{"type", "uri"}, {"target", "com.epicgames.launcher://apps/a%3Ab%3Ac?action=launch"}},
        app, error));
    EXPECT_EQ(app.type, AppType::kUri);
    EXPECT_FALSE(
        couchd::parse_app("a", {{"type", "uri"}, {"target", "file:///C:/evil.exe"}}, app, error));
    EXPECT_FALSE(couchd::parse_app("a", {{"type", "uri"}, {"target", "ms-settings:"}}, app, error));
    EXPECT_FALSE(couchd::parse_app(
        "a", {{"type", "uri"}, {"target", "com.epicgames.launcher://a b"}}, app, error));
}

TEST(ParseApp, ReadsHowToWatchALauncher) {
    AppSpec app;
    std::string error;
    EXPECT_TRUE(couchd::parse_app(
        "a",
        {{"type", "exe"}, {"target", "C:\\a.exe"}, {"installFolder", "C:\\a"}, {"watch", "window"}},
        app, error));
    EXPECT_TRUE(app.watch_windows);
    EXPECT_EQ(app.install_folder, "C:\\a");
}

TEST(ParseApp, ReadsAnAppFromTheAllAppsList) {
    AppSpec app;
    std::string error;
    EXPECT_TRUE(couchd::parse_app(
        "a", {{"type", "shell"}, {"target", "shell:AppsFolder\\Spotify_abc!Spotify"}}, app, error));
    EXPECT_EQ(app.type, AppType::kShell);
    EXPECT_TRUE(couchd::parse_app(
        "a", {{"type", "shell"}, {"target", "shell:AppsFolder\\C:\\My Apps\\a.exe"}}, app, error));

    EXPECT_FALSE(
        couchd::parse_app("a", {{"type", "shell"}, {"target", "C:\\evil.exe"}}, app, error));
    EXPECT_FALSE(couchd::parse_app(
        "a", {{"type", "shell"}, {"target", "shell:AppsFolder\\a\" --evil"}}, app, error));
}

TEST_F(AgentTest, ShellAppKeepsCouchShellVisibleUntilItsWindowAppears) {
    bus.hashes["app:any"] = {{"type", "shell"}, {"target", "shell:AppsFolder\\x!y"}};
    platform.process = ProcessState::kNotFound;
    agent.handle_request("launch any");
    EXPECT_TRUE(bus.events().empty());

    platform.process = ProcessState::kRunning;
    advance(1000);
    EXPECT_EQ(bus.events(), (Events{"started any"}));
}

TEST(ParseApp, AcceptsEachType) {
    AppSpec app;
    std::string error;
    EXPECT_TRUE(couchd::parse_app("a", {{"type", "exe"}, {"target", "C:\\a.exe"}}, app, error));
    EXPECT_EQ(app.type, AppType::kExe);
    EXPECT_TRUE(
        couchd::parse_app("a", {{"type", "web"}, {"target", "https://a.com/x?y=1"}}, app, error));
    EXPECT_EQ(app.type, AppType::kWeb);
    EXPECT_TRUE(couchd::parse_app(
        "a", {{"type", "steam"}, {"target", "440"}, {"processName", "hl2.exe"}}, app, error));
    EXPECT_EQ(app.type, AppType::kSteam);
}

TEST(ParseApp, RejectsMissingOrUnsafeFields) {
    const std::vector<std::map<std::string, std::string>> bad = {
        {},
        {{"type", "exe"}},
        {{"type", "alien"}, {"target", "x"}},
        {{"type", "exe"}, {"target", "C:\\a\" --evil \".exe"}},
        {{"type", "web"}, {"target", "file:///C:/secret.txt"}},
        {{"type", "web"}, {"target", "https://a.com\" --evil"}},
        {{"type", "web"}, {"target", "https://a.com --evil"}},
        {{"type", "steam"}, {"target", "440; calc"}, {"processName", "hl2.exe"}},
        {{"type", "web"}, {"target", "https://a.com"}, {"userAgent", "x\" --evil"}},
    };
    for (std::size_t i = 0; i < bad.size(); ++i) {
        AppSpec app;
        std::string error;
        EXPECT_FALSE(couchd::parse_app("a", bad[i], app, error)) << "case #" << i;
        EXPECT_FALSE(error.empty()) << "case #" << i;
    }
}

}  // namespace
