#pragma once
#include "../WorkFor.Native.Contracts/Win32Support.h"
#include <atomic>
#include <mutex>
#include <thread>

namespace workfor::native {
struct RuntimeConfiguration {
    double widthDip{260};
    std::uint64_t configRevision{};
    std::uint64_t serial{};
    std::string requestId;
};

// The inherited stdin pipe is polled before reading, so shutdown never relies
// on canceling a potentially not-yet-entered synchronous ReadFile operation.
class RuntimeControl final {
public:
    explicit RuntimeControl(double initialWidth, HANDLE input = GetStdHandle(STD_INPUT_HANDLE));
    ~RuntimeControl();
    RuntimeControl(const RuntimeControl&) = delete;
    RuntimeControl& operator=(const RuntimeControl&) = delete;
    bool Stopping() const noexcept;
    bool Wait(DWORD milliseconds) const noexcept;
    void Stop() noexcept;
    RuntimeConfiguration Configuration() const;
    std::vector<nlohmann::json> TakeNotifications();
private:
    void Read() noexcept;
    void Command(const nlohmann::json& message);
    void Notify(std::string code, std::string requestId = {});
    UniqueHandle stopped_;
    HANDLE input_{};
    mutable std::mutex gate_;
    RuntimeConfiguration configuration_;
    std::vector<nlohmann::json> notifications_;
    std::thread reader_;
};

nlohmann::json RunRuntimeControlSelfTests();
}
