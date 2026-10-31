// couchd's entry point: connects to Tachyon and runs the agent until Ctrl+C.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h>
#include <ws2tcpip.h>

// After winsock2.h, which must come before windows.h.
#include <objbase.h>

#include <chrono>
#include <csignal>
#include <cstddef>
#include <iostream>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#include "agent/agent.h"
#include "platform/win32/windows_platform.h"
#include "protocol/reply_parser.h"

namespace couchd {

namespace {

volatile std::sig_atomic_t g_stop_requested = 0;

void on_stop_signal(int) { g_stop_requested = 1; }

const unsigned short kDefaultPort = 6380;
const int kWaitSliceMs = 100;        // how long the main loop sleeps when nothing is happening
const int kCommandTimeoutMs = 3000;  // Tachyon is local, so a slower reply means it's stuck
const int kRetryDelayMs = 500;

// One TCP connection to Tachyon, closed when destroyed.
class Connection {
public:
    Connection() = default;
    ~Connection() { close(); }
    Connection(const Connection&) = delete;
    Connection& operator=(const Connection&) = delete;

    bool connected() const { return socket_ != INVALID_SOCKET; }

    bool open(unsigned short port) {
        close();
        socket_ = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (socket_ == INVALID_SOCKET) return false;
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_port = htons(port);
        inet_pton(AF_INET, "127.0.0.1", &address.sin_addr);
        if (connect(socket_, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) ==
            SOCKET_ERROR) {
            close();
            return false;
        }
        // Small requests and replies go out at once, rather than waiting to be batched.
        BOOL no_delay = TRUE;
        setsockopt(socket_, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&no_delay),
                   sizeof(no_delay));
        parser_ = ReplyParser();
        return true;
    }

    void close() {
        if (socket_ != INVALID_SOCKET) closesocket(socket_);
        socket_ = INVALID_SOCKET;
    }

    bool send_all(std::string_view data) {
        while (!data.empty()) {
            int sent = send(socket_, data.data(), static_cast<int>(data.size()), 0);
            if (sent == SOCKET_ERROR) {
                close();
                return false;
            }
            data.remove_prefix(static_cast<std::size_t>(sent));
        }
        return true;
    }

    // Waits up to timeout_ms for a whole reply. kNeedMore means the time ran out. A closed or
    // broken connection gives kError and closes this one.
    ParseStatus read_reply(Reply& reply, int timeout_ms) {
        ParseStatus status = parser_.next(reply);
        if (status != ParseStatus::kNeedMore) return checked(status);

        fd_set readable;
        FD_ZERO(&readable);
        FD_SET(socket_, &readable);
        timeval timeout{};
        timeout.tv_sec = timeout_ms / 1000;
        timeout.tv_usec = (timeout_ms % 1000) * 1000;
        int ready = select(0, &readable, nullptr, nullptr, &timeout);
        if (ready == 0) return ParseStatus::kNeedMore;
        if (ready == SOCKET_ERROR) return checked(ParseStatus::kError);

        char buffer[4096];
        int n = recv(socket_, buffer, sizeof(buffer), 0);
        if (n <= 0) return checked(ParseStatus::kError);  // 0: Tachyon closed the connection
        parser_.feed(std::string_view(buffer, static_cast<std::size_t>(n)));
        return checked(parser_.next(reply));
    }

private:
    ParseStatus checked(ParseStatus status) {
        if (status == ParseStatus::kError) close();
        return status;
    }

    SOCKET socket_ = INVALID_SOCKET;
    ReplyParser parser_;
};

// The Bus the agent uses: one command at a time, waiting for each reply.
class TachyonBus : public Bus {
public:
    explicit TachyonBus(Connection& connection) : connection_(connection) {}

    bool run(const std::vector<std::string>& args, Reply& reply) override {
        if (!connection_.connected() || !connection_.send_all(encode_command(args))) return false;
        for (int waited = 0; waited < kCommandTimeoutMs; waited += kWaitSliceMs) {
            ParseStatus status = connection_.read_reply(reply, kWaitSliceMs);
            if (status == ParseStatus::kReply) return true;
            if (status == ParseStatus::kError) return false;
        }
        connection_.close();  // a late reply would be mistaken for the next command's
        return false;
    }

private:
    Connection& connection_;
};

// Connects both connections and subscribes. Returns false if Tachyon isn't reachable.
bool connect_to_tachyon(unsigned short port, Connection& commands, Connection& requests) {
    if (!commands.open(port) || !requests.open(port)) {
        commands.close();
        requests.close();
        return false;
    }
    Reply confirmation;
    bool subscribed = requests.send_all(encode_command({"SUBSCRIBE", kRequestChannel}));
    for (int waited = 0; subscribed && waited < kCommandTimeoutMs; waited += kWaitSliceMs) {
        ParseStatus status = requests.read_reply(confirmation, kWaitSliceMs);
        if (status == ParseStatus::kReply) return true;
        if (status == ParseStatus::kError) break;
    }
    commands.close();
    requests.close();
    return false;
}

// Runs one published request, if one has arrived; otherwise waits briefly.
void process_requests(Connection& requests, Agent& agent) {
    Reply message;
    ParseStatus status = requests.read_reply(message, kWaitSliceMs);
    while (status == ParseStatus::kReply) {
        if (message.items.size() == 3 && message.items[0].text == "message") {
            agent.handle_request(message.items[2].text);
        }
        status = requests.read_reply(message, 0);
    }
}

bool parse_args(int argc, char* argv[], unsigned short& port, bool& dry_run_power) {
    for (int i = 1; i < argc; ++i) {
        std::string_view arg = argv[i];
        if (arg == "--dry-run-power") {
            dry_run_power = true;
        } else if (arg == "--port" && i + 1 < argc) {
            int value = std::atoi(argv[++i]);
            if (value < 1 || value > 65535) return false;
            port = static_cast<unsigned short>(value);
        } else {
            return false;
        }
    }
    return true;
}

// Stops COM when destroyed.
class ComLibrary {
public:
    ComLibrary() : ok_(SUCCEEDED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED))) {}
    ~ComLibrary() {
        if (ok_) CoUninitialize();
    }
    ComLibrary(const ComLibrary&) = delete;
    ComLibrary& operator=(const ComLibrary&) = delete;
    bool ok() const { return ok_; }

private:
    bool ok_;
};

// Stops Winsock when destroyed.
class SocketLibrary {
public:
    SocketLibrary() {
        WSADATA data;
        ok_ = WSAStartup(MAKEWORD(2, 2), &data) == 0;
    }
    ~SocketLibrary() {
        if (ok_) WSACleanup();
    }
    SocketLibrary(const SocketLibrary&) = delete;
    SocketLibrary& operator=(const SocketLibrary&) = delete;
    bool ok() const { return ok_; }

private:
    bool ok_ = false;
};

int run(int argc, char* argv[]) {
    unsigned short port = kDefaultPort;
    bool dry_run_power = false;
    if (!parse_args(argc, argv, port, dry_run_power)) {
        std::cerr << "usage: couchd [--port N] [--dry-run-power]" << std::endl;
        return 1;
    }
    if (std::signal(SIGINT, on_stop_signal) == SIG_ERR ||
        std::signal(SIGTERM, on_stop_signal) == SIG_ERR) {
        std::cerr << "couchd: failed to install signal handlers" << std::endl;
        return 1;
    }

    SocketLibrary sockets;
    ComLibrary com;  // for ShellExecuteEx and Core Audio
    if (!sockets.ok() || !com.ok()) {
        std::cerr << "couchd: could not start Winsock or COM" << std::endl;
        return 1;
    }

    Connection commands;
    Connection requests;
    TachyonBus bus(commands);
    WindowsPlatform platform(dry_run_power);
    Agent agent(platform, bus);

    std::cout << "couchd: started, connecting to Tachyon on port " << port << std::endl;
    bool was_connected = false;
    while (!g_stop_requested) {
        if (!commands.connected() || !requests.connected()) {
            if (was_connected) std::cout << "couchd: lost Tachyon, retrying" << std::endl;
            was_connected = false;
            if (connect_to_tachyon(port, commands, requests)) {
                std::cout << "couchd: connected to Tachyon" << std::endl;
                was_connected = true;
                agent.on_connected();
            }
        }

        if (requests.connected()) {
            process_requests(requests, agent);
        } else {
            // The agent keeps watching the app while it waits for Tachyon to return.
            std::this_thread::sleep_for(std::chrono::milliseconds(kRetryDelayMs));
        }
        agent.tick();
    }

    std::cout << "couchd: stopped cleanly" << std::endl;
    return 0;
}

}  // namespace

}  // namespace couchd

int main(int argc, char* argv[]) { return couchd::run(argc, argv); }
