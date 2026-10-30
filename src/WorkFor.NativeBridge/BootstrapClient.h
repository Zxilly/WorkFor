#pragma once
#include "TargetIdentity.h"

namespace workfor::native {
// P0 deliberately limits hooking to an owned probe process. There is no general
// arbitrary-PID/DLL command-line injection interface.
nlohmann::json RunBootstrapExperiment(const std::filesystem::path& imagePath);
nlohmann::json RunExplorerInspection(const std::filesystem::path& imagePath);
nlohmann::json RunExplorerLiveExperiment(const std::filesystem::path& imagePath, unsigned durationSeconds);
nlohmann::json RunExplorerContinuous(const std::filesystem::path& imagePath);
int RunProbeTarget(const std::wstring& readyEvent);
}
