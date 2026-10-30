#pragma once
#include "TargetIdentity.h"
#include <atomic>
#include <thread>

namespace workfor::native {
// Owns the window/message-pump required by TaskbarCreated and out-of-context
// WinEvents. Events are coalesced into a revision; no token/file IO in callbacks.
class DiscoveryWatcher final {
public:
    DiscoveryWatcher();
    ~DiscoveryWatcher();
    DiscoveryWatcher(const DiscoveryWatcher&) = delete;
    DiscoveryWatcher& operator=(const DiscoveryWatcher&) = delete;
    std::uint64_t revision() const noexcept { return revision_.load(); }
    void Notify() noexcept { revision_.fetch_add(1, std::memory_order_relaxed); }
private:
    void Run() noexcept;
    static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam);
    std::atomic<std::uint64_t> revision_{1};
    UniqueHandle ready_;
    std::thread thread_;
    HWND window_{};
    std::exception_ptr error_;
    UINT taskbarCreated_{};
};

int RunControlStdio();
}
