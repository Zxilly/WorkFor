#include "TargetIdentity.h"

namespace workfor::native {
TargetIdentity InspectTarget(HWND window) {
    if (!IsWindow(window)) throw Win32Error("Invalid target window", ERROR_INVALID_WINDOW_HANDLE);
    TargetIdentity target;
    target.window = window;
    target.threadId = GetWindowThreadProcessId(window, &target.processId);
    if (!target.threadId || !target.processId) throw Win32Error("GetWindowThreadProcessId");
    target.process.reset(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE,
        FALSE, target.processId));
    if (!target.process) throw Win32Error("OpenProcess(target)");
    target.imagePath = ProcessImagePath(target.process.get());
    target.userSid = TokenSid(target.process.get());
    target.sessionId = ProcessSession(target.processId);
    target.integrityLevel = IntegrityLevel(target.process.get());
    target.creationTime = ProcessCreationTime(target.process.get());
    target.nativeX64 = IsNativeX64(target.process.get());
    std::array<wchar_t, 256> className{};
    if (!GetClassNameW(window, className.data(), static_cast<int>(className.size())))
        throw Win32Error("GetClassNameW");
    target.windowClass = className.data();
    return target;
}

void ValidateSameUserTarget(const TargetIdentity& target) {
    const auto ownIntegrity = IntegrityLevel(GetCurrentProcess());
    if (target.userSid != TokenSid(GetCurrentProcess()) ||
        target.sessionId != ProcessSession(GetCurrentProcessId()) ||
        !target.nativeX64 || !IsNativeX64(GetCurrentProcess()) ||
        target.integrityLevel != ownIntegrity ||
        (ownIntegrity != SECURITY_MANDATORY_MEDIUM_RID &&
            ownIntegrity != SECURITY_MANDATORY_HIGH_RID))
        throw Win32Error("TARGET_IDENTITY_MISMATCH", ERROR_ACCESS_DENIED);
    if (WaitForSingleObject(target.process.get(), 0) != WAIT_TIMEOUT)
        throw Win32Error("Target exited", ERROR_PROCESS_ABORTED);
    DWORD currentPid{};
    if (GetWindowThreadProcessId(target.window, &currentPid) != target.threadId ||
        currentPid != target.processId ||
        ProcessCreationTime(target.process.get()) != target.creationTime)
        throw Win32Error("Target identity changed", ERROR_INVALID_WINDOW_HANDLE);
}

void ValidateExplorerTarget(const TargetIdentity& target) {
    ValidateSameUserTarget(target);
    std::array<wchar_t, MAX_PATH> windows{};
    if (!GetWindowsDirectoryW(windows.data(), static_cast<UINT>(windows.size())))
        throw Win32Error("GetWindowsDirectoryW");
    const auto expected = std::filesystem::path(windows.data()) / L"explorer.exe";
    if (_wcsicmp(expected.c_str(), target.imagePath.c_str()) != 0 ||
        (target.windowClass != L"Shell_TrayWnd" && target.windowClass != L"Shell_SecondaryTrayWnd"))
        throw Win32Error("Expected system Explorer taskbar", ERROR_ACCESS_DENIED);
}

void ValidateProbeTarget(const TargetIdentity& target, DWORD expectedPid,
    std::uint64_t expectedCreationTime, const std::filesystem::path& expectedImage) {
    ValidateSameUserTarget(target);
    if (target.processId != expectedPid || target.creationTime != expectedCreationTime ||
        target.windowClass != kProbeWindowClass ||
        _wcsicmp(target.imagePath.c_str(), expectedImage.c_str()) != 0)
        throw Win32Error("Probe target identity mismatch", ERROR_ACCESS_DENIED);
}

nlohmann::json Describe(const TargetIdentity& target) {
    return {
        {"processId", target.processId}, {"threadId", target.threadId},
        {"sessionId", target.sessionId}, {"integrityLevel", target.integrityLevel},
        {"creationTime", std::to_string(target.creationTime)},
        {"window", std::to_string(reinterpret_cast<std::uintptr_t>(target.window))},
        {"nativeX64", target.nativeX64}, {"imagePath", Utf8(target.imagePath)},
        {"userSid", Utf8(target.userSid)}, {"windowClass", Utf8(target.windowClass)}
    };
}

std::vector<TargetIdentity> DiscoverTaskbars() {
    struct Discovery { std::vector<HWND> windows; } discovery;
    EnumWindows([](HWND window, LPARAM parameter) -> BOOL {
        std::array<wchar_t, 256> name{};
        if (!GetClassNameW(window, name.data(), static_cast<int>(name.size()))) return TRUE;
        if (wcscmp(name.data(), L"Shell_TrayWnd") == 0 ||
            wcscmp(name.data(), L"Shell_SecondaryTrayWnd") == 0)
            reinterpret_cast<Discovery*>(parameter)->windows.push_back(window);
        return TRUE;
    }, reinterpret_cast<LPARAM>(&discovery));
    std::vector<TargetIdentity> result;
    for (const auto window : discovery.windows) result.push_back(InspectTarget(window));
    return result;
}
}
