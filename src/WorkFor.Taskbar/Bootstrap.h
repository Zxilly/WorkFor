#pragma once
#include <Windows.h>
#include <cstdint>

extern "C" __declspec(dllexport)
LRESULT CALLBACK WorkForBootstrapHook(int code, WPARAM wParam, LPARAM lParam);

extern "C" __declspec(dllexport)
std::uint32_t WINAPI WorkForGetNativeAbiVersion();

// This P0 module only proves the bootstrap/IPC/dormant lifecycle. Taskbar layout
// integration must dispatch onto the real XAML owner thread and report Attached
// only after the independent adapter has verified layout ownership.
