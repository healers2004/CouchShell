#include <chrono>
#include <csignal>
#include <iostream>
#include <thread>

namespace {  // anonymous namespace: visible only in this file, like `static` in C

// Set by the signal handler, read by main. sig_atomic_t is the one type a handler may safely write.
// volatile: main must re-read it every loop instead of caching it.
volatile std::sig_atomic_t g_stop_requested = 0;

// Runs on Ctrl+C (SIGINT) or `kill` (SIGTERM). Only sets the flag: a handler must not do I/O.
// On Windows the C runtime calls this on a separate thread; the flag is still enough here.
void on_stop_signal(int) { g_stop_requested = 1; }

}  // namespace

int main() {
    // std::signal returns SIG_ERR if the handler can't be installed.
    if (std::signal(SIGINT, on_stop_signal) == SIG_ERR ||
        std::signal(SIGTERM, on_stop_signal) == SIG_ERR) {
        std::cerr << "couchd: failed to install signal handlers\n";
        return 1;
    }

    // std::endl flushes, so the line shows immediately.
    std::cout << "couchd: started, press Ctrl+C to stop" << std::endl;

    // shortcut: wakes every 100 ms to check the flag; replaced by the event loop in Phase 2.
    while (!g_stop_requested) {
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }

    std::cout << "couchd: stopped cleanly" << std::endl;
    return 0;
}
