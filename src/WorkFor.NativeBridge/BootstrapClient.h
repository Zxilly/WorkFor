#pragma once
#include "TargetIdentity.h"

namespace workfor::native {
class BridgeFailure : public std::runtime_error {
public:
    std::string code;
    DWORD targetProcessId{};
    std::uint64_t targetCreationTime{};
    BridgeFailure(std::string stableCode, const std::string& message,
        DWORD processId = 0, std::uint64_t creationTime = 0)
        : std::runtime_error(message), code(std::move(stableCode)),
          targetProcessId(processId), targetCreationTime(creationTime) {}
};
std::string ClassifyNativeFailure(const std::exception& error);
// P0 deliberately limits hooking to an owned probe process. There is no general
// arbitrary-PID/DLL command-line injection interface.
nlohmann::json RunBootstrapExperiment(const std::filesystem::path& imagePath);
nlohmann::json RunExplorerInspection(const std::filesystem::path& imagePath);
nlohmann::json RunExplorerLiveExperiment(const std::filesystem::path& imagePath, unsigned durationSeconds, double widthDip = 260.0);
nlohmann::json RunExplorerContinuous(const std::filesystem::path& imagePath, double widthDip = 260.0);
int RunProbeTarget(const std::wstring& readyEvent);
}
