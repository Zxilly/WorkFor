#pragma once
#include "../WorkFor.Native.Contracts/Win32Support.h"

namespace workfor::native {
struct TargetIdentity {
    DWORD processId{};
    DWORD threadId{};
    DWORD sessionId{};
    DWORD integrityLevel{};
    std::uint64_t creationTime{};
    HWND window{};
    bool nativeX64{};
    std::wstring imagePath;
    std::wstring userSid;
    std::wstring windowClass;
    UniqueHandle process;
};

TargetIdentity InspectTarget(HWND window);
void ValidateSameUserTarget(const TargetIdentity& target);
void ValidateExplorerTarget(const TargetIdentity& target);
void ValidateProbeTarget(const TargetIdentity& target, DWORD expectedPid,
    std::uint64_t expectedCreationTime, const std::filesystem::path& expectedImage);
nlohmann::json Describe(const TargetIdentity& target);
std::vector<TargetIdentity> DiscoverTaskbars();
}
