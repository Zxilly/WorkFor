#pragma once

#include <windows.h>
#ifdef GetCurrentTime
#undef GetCurrentTime
#endif
#include <string>
#include <winrt/Windows.UI.Xaml.h>

namespace workfor::taskbar {

inline constexpr char kResearchTaskbarSha256[] =
    "5dceea036939acb4e43b801a7a63530f328edad164741e6529d3d32e9b1c5866";
inline constexpr wchar_t kResearchTaskbarPdbGuidAge[] = L"483327DB5FAC66CA1D844F9D8F5054CB1";

// File/symbol evidence is not a promise that a Windows build has passed layout tests.
struct ModuleFingerprint {
    std::wstring path;
    std::wstring fileVersion;
    std::string sha256;
};

HRESULT FingerprintModuleFile(const std::wstring& path, ModuleFingerprint& result) noexcept;

// File IO/SHA must run on the control worker before dispatching any UI work.
// Resolver only consumes this exact-module proof plus in-memory byte checks.
HRESULT PrepareTaskbarCompatibility(std::wstring& diagnostic) noexcept;

// Must run inside the taskbar Explorer on the owning taskbar UI thread.
// This strictly matched research resolver performs no UI mutation or inline hook.
HRESULT ResolveCurrentTaskbarRoot(
    HWND taskbarWindow,
    winrt::Windows::UI::Xaml::FrameworkElement& root,
    std::wstring& diagnostic) noexcept;

} // namespace workfor::taskbar
