#pragma once

#include <windows.h>
#include <string>

namespace workfor::taskbar {

struct Activation {
    HWND taskbarWindow{};
    RECT screenPhysicalBounds{};
    UINT dpi{};
};
using ActivationHandler = void (*)(const Activation&, void* context) noexcept;
// UI-thread only. Handler must enqueue work and return; never block on IPC.
void AdapterSetActivationHandler(ActivationHandler handler, void* context) noexcept;

// Calls must execute on the taskbar's XAML owner thread. Inspect never mutates UI.
HRESULT AdapterInspect(HWND taskbarWindow, std::string& json) noexcept;

// Experimental, opt-in only: strict fingerprint and structural validation are
// required before adding a real Grid column. No overlay and no inline hook.
HRESULT AdapterAttach(HWND taskbarWindow, double widthDip, std::wstring& diagnostic) noexcept;
HRESULT AdapterUpdate(const std::wstring& amount, const std::wstring& status) noexcept;
HRESULT AdapterDetach(std::wstring& diagnostic) noexcept;

} // namespace workfor::taskbar
