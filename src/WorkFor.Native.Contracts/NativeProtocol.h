#pragma once

#include <Windows.h>
#include <cstdint>
#include <string>

namespace workfor::native {
inline constexpr std::uint32_t kNativeAbiVersion = 1;
inline constexpr char kNativeVersion[] = "0.1.0-p0";
inline constexpr wchar_t kBootstrapMessage[] = L"WorkFor.Native.Bootstrap.v1";
inline constexpr wchar_t kProbeWindowClass[] = L"WorkFor.ProbeTarget.v1";
inline constexpr DWORD kMaxFrameBytes = 16 * 1024;
inline constexpr DWORD kHandshakeTimeoutMs = 5'000;
inline constexpr DWORD kCommandTimeoutMs = 5'000;
inline constexpr DWORD kMaxInspectionBytes = 256 * 1024;

// The P0 experiment uses a private endpoint unique to its owning bridge and
// attempt. Production endpoints/leases are a separate, compatibility-gated path.
inline std::wstring ProbePipeName(DWORD bridgePid, std::uint32_t attempt) {
    return L"\\\\.\\pipe\\workfor-native-probe-v1-" +
        std::to_wstring(bridgePid) + L"-" + std::to_wstring(attempt);
}
}
