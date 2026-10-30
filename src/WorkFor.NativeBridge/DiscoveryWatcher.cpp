#include "DiscoveryWatcher.h"

namespace workfor::native {
namespace {
constexpr wchar_t kControlWindowClass[] = L"WorkFor.NativeDiscovery.v1";
constexpr UINT kDiscoveryChanged = WM_APP + 7;
std::atomic<HWND> g_discoveryWindow{};

void CALLBACK OnWinEvent(HWINEVENTHOOK, DWORD, HWND window, LONG objectId,
    LONG childId, DWORD, DWORD) noexcept {
    if (!window || objectId != OBJID_WINDOW || childId != CHILDID_SELF) return;
    std::array<wchar_t, 256> name{};
    if (!GetClassNameW(window, name.data(), static_cast<int>(name.size()))) return;
    if (wcscmp(name.data(), L"Shell_TrayWnd") != 0 &&
        wcscmp(name.data(), L"Shell_SecondaryTrayWnd") != 0) return;
    if (const auto target = g_discoveryWindow.load(std::memory_order_acquire))
        PostMessageW(target, kDiscoveryChanged, 0, 0);
}
}

DiscoveryWatcher::DiscoveryWatcher()
    : ready_(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {
    if (!ready_) throw Win32Error("CreateEventW(discovery)");
    thread_ = std::thread(&DiscoveryWatcher::Run, this);
    const auto wait = WaitForSingleObject(ready_.get(), 5'000);
    if (wait != WAIT_OBJECT_0) {
        // Startup owns only its own thread. The readiness operation performs no
        // remote calls, so joining cannot wait for a hung Explorer UI thread.
        if (window_) PostMessageW(window_, WM_CLOSE, 0, 0);
        thread_.join();
        throw Win32Error("Discovery startup deadline", ERROR_TIMEOUT);
    }
    if (error_) {
        thread_.join();
        std::rethrow_exception(error_);
    }
}

DiscoveryWatcher::~DiscoveryWatcher() {
    if (window_) PostMessageW(window_, WM_CLOSE, 0, 0);
    if (thread_.joinable()) thread_.join();
}

LRESULT CALLBACK DiscoveryWatcher::WindowProc(HWND window, UINT message,
    WPARAM wParam, LPARAM lParam) {
    if (message == WM_NCCREATE) {
        const auto create = reinterpret_cast<CREATESTRUCTW*>(lParam);
        SetWindowLongPtrW(window, GWLP_USERDATA,
            reinterpret_cast<LONG_PTR>(create->lpCreateParams));
    }
    auto* watcher = reinterpret_cast<DiscoveryWatcher*>(
        GetWindowLongPtrW(window, GWLP_USERDATA));
    if (watcher && (message == watcher->taskbarCreated_ ||
        message == kDiscoveryChanged || message == WM_TIMER)) {
        watcher->Notify();
        return 0;
    }
    if (message == WM_CLOSE) { DestroyWindow(window); return 0; }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wParam, lParam);
}

void DiscoveryWatcher::Run() noexcept {
    HWINEVENTHOOK hook{};
    try {
        taskbarCreated_ = RegisterWindowMessageW(L"TaskbarCreated");
        if (!taskbarCreated_) throw Win32Error("RegisterWindowMessageW(TaskbarCreated)");
        WNDCLASSW windowClass{};
        windowClass.hInstance = GetModuleHandleW(nullptr);
        windowClass.lpfnWndProc = WindowProc;
        windowClass.lpszClassName = kControlWindowClass;
        if (!RegisterClassW(&windowClass) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
            throw Win32Error("RegisterClassW(discovery)");
        window_ = CreateWindowExW(WS_EX_TOOLWINDOW, kControlWindowClass,
            L"WorkFor native discovery (hidden)", WS_OVERLAPPED,
            0, 0, 1, 1, nullptr, nullptr, windowClass.hInstance, this);
        if (!window_) throw Win32Error("CreateWindowExW(discovery)");
        g_discoveryWindow.store(window_, std::memory_order_release);
        hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_DESTROY, nullptr,
            OnWinEvent, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (!hook) throw Win32Error("SetWinEventHook(discovery)");
        if (!SetTimer(window_, 1, 30'000, nullptr)) throw Win32Error("SetTimer(discovery)");
        SetEvent(ready_.get());
        MSG message{};
        BOOL result{};
        while ((result = GetMessageW(&message, nullptr, 0, 0)) > 0) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        if (result == -1) throw Win32Error("GetMessageW(discovery)");
    } catch (...) {
        error_ = std::current_exception();
        SetEvent(ready_.get());
    }
    g_discoveryWindow.store(nullptr, std::memory_order_release);
    if (hook) UnhookWinEvent(hook);
    if (window_ && IsWindow(window_)) DestroyWindow(window_);
}
}
