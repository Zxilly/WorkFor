#pragma once

#include "Compatibility.h"

namespace workfor::taskbar {

// The same layout algorithm used by AdapterAttach, separated from Explorer
// discovery so its space reservation and reversal can be tested in our own UI.
HRESULT AdapterAttachToRoot(const winrt::Windows::UI::Xaml::FrameworkElement& root,
    double widthDip, std::wstring& diagnostic, HWND hostWindow = nullptr) noexcept;

} // namespace workfor::taskbar
