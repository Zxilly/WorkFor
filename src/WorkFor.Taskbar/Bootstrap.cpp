#include "Bootstrap.h"
#include "Adapter.h"
#include "Compatibility.h"
#include "../WorkFor.Native.Contracts/Win32Support.h"
#include <atomic>
#include <mutex>
#include <unordered_map>
#include <thread>

using namespace workfor::native;

namespace {
HMODULE g_module{};
std::atomic<bool> g_running{false};
std::atomic<bool> g_attached{false};
std::atomic<HWND> g_controlWindow{};
constexpr UINT kUiCommandMessage = WM_APP + 101;
struct UiCommand {
    enum class Kind { Attach, Update, Configure, Inspect, Detach } kind{};
    double widthDip{260};
    std::wstring amount, status, title, diagnostic;
    std::string inspectionJson;
    HRESULT result{E_PENDING};
    UniqueHandle completed{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
};
std::mutex g_commandsGate;
std::unordered_map<UINT_PTR, std::shared_ptr<UiCommand>> g_commands;
std::atomic<UINT_PTR> g_nextCommand{1};
std::mutex g_activationGate;
std::atomic<bool> g_activationPending{false};
workfor::taskbar::Activation g_activation{};
std::atomic<DWORD> g_displayServerPid{};
std::atomic<HANDLE> g_activationEvent{};
std::atomic<unsigned> g_clickFramesSent{};
std::atomic<unsigned> g_displayConnections{};

void OnActivation(const workfor::taskbar::Activation& activation, void*) noexcept {
    // User input runs inside Explorer. Grant the connected Host foreground
    // permission before it starts/activates its UI; do no pipe IO here.
    const auto hostPid = g_displayServerPid.load();
    if (!hostPid) return; // Do not replay an old user click after a later reconnect.
    AllowSetForegroundWindow(hostPid);
    {
        std::lock_guard lock(g_activationGate);
        g_activation = activation;
    }
    g_activationPending.store(true, std::memory_order_release);
    if (const auto ready = g_activationEvent.load()) SetEvent(ready);
}

LRESULT CALLBACK ControlWindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    if (message == WM_NCDESTROY && g_controlWindow.load() == window) {
        std::wstring diagnostic;
        if (SUCCEEDED(workfor::taskbar::AdapterDetach(diagnostic))) g_attached.store(false);
        g_controlWindow.store(nullptr);
    }
    if (message == kUiCommandMessage) {
        std::shared_ptr<UiCommand> command;
        {
            std::lock_guard lock(g_commandsGate);
            const auto found = g_commands.find(wParam);
            if (found == g_commands.end()) return 0;
            command = std::move(found->second);
            g_commands.erase(found);
        }
        if (command->kind == UiCommand::Kind::Attach) {
            workfor::taskbar::AdapterSetActivationHandler(OnActivation, nullptr);
            command->result = workfor::taskbar::AdapterAttach(
                reinterpret_cast<HWND>(GetWindowLongPtrW(window, GWLP_USERDATA)),
                command->widthDip, command->diagnostic);
            g_attached.store(workfor::taskbar::AdapterHasLease());
        } else if (command->kind == UiCommand::Kind::Update) {
            command->result = workfor::taskbar::AdapterUpdate(command->amount, command->status, command->title);
        } else if (command->kind == UiCommand::Kind::Configure) {
            command->result = workfor::taskbar::AdapterConfigure(command->widthDip, command->diagnostic);
        } else if (command->kind == UiCommand::Kind::Inspect) {
            command->result = workfor::taskbar::AdapterInspect(
                reinterpret_cast<HWND>(GetWindowLongPtrW(window, GWLP_USERDATA)), command->inspectionJson);
        } else {
            command->result = workfor::taskbar::AdapterDetach(command->diagnostic);
            if (SUCCEEDED(command->result)) {
                workfor::taskbar::AdapterSetActivationHandler(nullptr, nullptr);
                g_activationPending.store(false);
                g_attached.store(false);
            }
        }
        SetEvent(command->completed.get());
        return 0;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}

HWND CreateControlWindow(HWND taskbarWindow) {
    const auto name = L"WorkFor.Taskbar.Control.v1." +
        std::to_wstring(reinterpret_cast<UINT_PTR>(g_module));
    WNDCLASSW windowClass{};
    windowClass.hInstance = g_module;
    windowClass.lpszClassName = name.c_str();
    windowClass.lpfnWndProc = ControlWindowProc;
    if (!RegisterClassW(&windowClass) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
        throw Win32Error("RegisterClassW(native control)");
    HWND window = CreateWindowExW(0, name.c_str(), L"", 0, 0, 0, 0, 0,
        HWND_MESSAGE, nullptr, g_module, nullptr);
    if (!window) throw Win32Error("CreateWindowExW(native control)");
    SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(taskbarWindow));
    return window;
}

HRESULT InvokeUi(const std::shared_ptr<UiCommand>& command, DWORD timeoutMs = 5'000) {
    if (!command->completed) return HRESULT_FROM_WIN32(GetLastError());
    const auto window = g_controlWindow.load();
    if (!window) return HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE);
    const auto ticket = g_nextCommand.fetch_add(1);
    { std::lock_guard lock(g_commandsGate); g_commands.emplace(ticket, command); }
    if (!PostMessageW(window, kUiCommandMessage, ticket, 0)) {
        std::lock_guard lock(g_commandsGate); g_commands.erase(ticket);
        return HRESULT_FROM_WIN32(GetLastError());
    }
    const auto wait = WaitForSingleObject(command->completed.get(), timeoutMs);
    if (wait != WAIT_OBJECT_0) {
        // UI may already own a shared_ptr and be running this operation. Never
        // free its arguments on timeout; remove only a still-queued command.
        std::lock_guard lock(g_commandsGate); g_commands.erase(ticket);
        return HRESULT_FROM_WIN32(wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError());
    }
    return command->result;
}

std::uint64_t UnixNowMs() {
    FILETIME time{}; GetSystemTimeAsFileTime(&time);
    const auto ticks = (static_cast<std::uint64_t>(time.dwHighDateTime) << 32) | time.dwLowDateTime;
    return (ticks - 116444736000000000ULL) / 10'000;
}

class ActivationWriter final {
public:
    explicit ActivationWriter(HANDLE pipe) : pipe_(pipe), stop_(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {
        if (!stop_) throw Win32Error("CreateEventW(activation writer stop)");
        HANDLE ready = g_activationEvent.load();
        if (!ready) {
            HANDLE created = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            if (!created) throw Win32Error("CreateEventW(activation ready)");
            if (!g_activationEvent.compare_exchange_strong(ready, created)) CloseHandle(created);
            else ready = created;
        }
        ready_ = ready;
        if (g_activationPending.load()) SetEvent(ready_);
        thread_ = std::thread([this] { Run(); });
    }
    ~ActivationWriter() { SetEvent(stop_.get()); if (thread_.joinable()) thread_.join(); }
private:
    void Run() noexcept {
        try {
            const HANDLE events[]{stop_.get(), ready_};
            while (WaitForMultipleObjects(2, events, FALSE, INFINITE) == WAIT_OBJECT_0 + 1) {
                if (!g_activationPending.exchange(false, std::memory_order_acq_rel)) continue;
                workfor::taskbar::Activation activation;
                { std::lock_guard lock(g_activationGate); activation = g_activation; }
                const auto& rect = activation.screenPhysicalBounds;
                WriteFrame(pipe_, {{"protocolVersion", 1}, {"type", "ui.openDetails"},
                    {"requestId", "native-click"}, {"payload", {
                        {"coordinateType", "physicalPixels"}, {"dpi", activation.dpi},
                        {"anchorRect", {{"x", rect.left}, {"y", rect.top},
                            {"width", rect.right - rect.left}, {"height", rect.bottom - rect.top}}}
                    }}}, 1'000);
                g_clickFramesSent.fetch_add(1);
            }
        } catch (...) {
            // A failed writer invalidates this duplex connection and wakes its
            // overlapped reader; the owner reconnects instead of losing clicks.
            CancelIoEx(pipe_, nullptr);
        }
    }
    HANDLE pipe_{};
    HANDLE ready_{};
    UniqueHandle stop_;
    std::thread thread_;
};

void DisplayWorker(std::atomic<bool>& stop, std::atomic<unsigned>& updateCount) noexcept {
    try {
        const auto name = L"\\\\.\\pipe\\workfor-display-v1-" + TokenSid(GetCurrentProcess()) +
            L"-" + std::to_wstring(ProcessSession(GetCurrentProcessId()));
        std::string generation;
        std::uint64_t sequence{};
        std::wstring lastAmount = L"--";
        while (!stop.load()) {
            try {
                if (!WaitNamedPipeW(name.c_str(), 500)) { Sleep(100); continue; }
                UniqueHandle pipe(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE,
                    0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr));
                if (!pipe) throw Win32Error("Open display pipe");
                ULONG pid{};
                if (!GetNamedPipeServerProcessId(pipe.get(), &pid)) throw Win32Error("Display server identity");
                UniqueHandle server(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
                if (!server || TokenSid(server.get()) != TokenSid(GetCurrentProcess()) ||
                    ProcessSession(pid) != ProcessSession(GetCurrentProcessId()) ||
                    IntegrityLevel(server.get()) != IntegrityLevel(GetCurrentProcess()) ||
                    _wcsicmp(std::filesystem::path(ProcessImagePath(server.get())).filename().c_str(), L"WorkFor.Host.exe") != 0)
                    throw Win32Error("Display server identity", ERROR_ACCESS_DENIED);
                WriteFrame(pipe.get(), {{"protocolVersion", 1}, {"type", "display.subscribe"},
                    {"requestId", "native-live"}, {"payload", nlohmann::json::object()}}, 1'000);
                g_displayServerPid.store(pid);
                g_displayConnections.fetch_add(1);
                ActivationWriter activationWriter(pipe.get());
                while (!stop.load()) {
                    const auto message = ReadFrame(pipe.get(), 1'500);
                    if (message.value("protocolVersion", 0) != 1 ||
                        message.value("type", "") != "display.snapshot" || !message.contains("payload"))
                        throw Win32Error("Invalid display snapshot", ERROR_INVALID_DATA);
                    const auto& payload = message.at("payload");
                    const auto nextGeneration = payload.at("generation").get<std::string>();
                    const auto nextSequence = payload.at("sequence").get<std::uint64_t>();
                    if (nextGeneration.empty() || nextGeneration.size() > 128) throw Win32Error("Invalid display generation", ERROR_INVALID_DATA);
                    if (generation == nextGeneration && nextSequence <= sequence) continue;
                    generation = nextGeneration; sequence = nextSequence;
                    auto amount = Wide(payload.value("amountText", "--"));
                    auto status = Wide(payload.value("statusText", ""));
                    auto title = Wide(payload.value("title", "收入"));
                    if (amount.size() > 80 || status.size() > 40 || title.size() > 40)
                        throw Win32Error("Display string too long", ERROR_INVALID_DATA);
                    if (payload.value("expiresAtMs", std::uint64_t{}) <= UnixNowMs()) status = L"等待更新";
                    auto command = std::make_shared<UiCommand>();
                    command->kind = UiCommand::Kind::Update;
                    command->amount = amount; command->status = std::move(status); command->title = std::move(title);
                    if (SUCCEEDED(InvokeUi(command))) { lastAmount = std::move(amount); updateCount.fetch_add(1); }
                }
            } catch (...) {
                g_displayServerPid.store(0);
                if (!stop.load()) {
                    auto command = std::make_shared<UiCommand>(); command->kind = UiCommand::Kind::Update;
                    command->amount = lastAmount; command->status = L"连接中";
                    InvokeUi(command, 1'000); Sleep(200);
                }
            }
        }
    } catch (...) {}
    g_displayServerPid.store(0);
}

struct BootstrapRequest {
    DWORD bridgePid{};
    std::uint32_t attempt{};
    DWORD targetThreadId{};
    bool inspectOnly{};
    std::string inspectionJson;
    bool liveCapable{};
};

bool IsProbeProcess() {
    const auto image = std::filesystem::path(ProcessImagePath(GetCurrentProcess()));
    return _wcsicmp(image.filename().c_str(), L"WorkFor.NativeBridge.exe") == 0;
}

bool IsSystemExplorer() {
    std::array<wchar_t, MAX_PATH> windows{};
    if (!GetWindowsDirectoryW(windows.data(), static_cast<UINT>(windows.size()))) return false;
    const auto expected = std::filesystem::path(windows.data()) / L"explorer.exe";
    return _wcsicmp(ProcessImagePath(GetCurrentProcess()).c_str(), expected.c_str()) == 0;
}

std::filesystem::path ExpectedBridgeImage() {
    std::wstring path(32768, L'\0');
    const DWORD length = GetModuleFileNameW(g_module, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) throw Win32Error("GetModuleFileNameW(Taskbar DLL)");
    path.resize(length);
    return std::filesystem::path(path).parent_path() / L"WorkFor.NativeBridge.exe";
}

DWORD WINAPI ControlWorker(void* parameter) noexcept {
    std::unique_ptr<BootstrapRequest> request(static_cast<BootstrapRequest*>(parameter));
    std::atomic<bool> stopDisplay{false};
    std::atomic<unsigned> updateCount{0};
    std::thread display;
    UniqueHandle activeMarker;
    bool detached = true;
    try {
        const auto pipeName = ProbePipeName(request->bridgePid, request->attempt);
        if (!WaitNamedPipeW(pipeName.c_str(), kHandshakeTimeoutMs))
            throw Win32Error("WaitNamedPipeW");
        UniqueHandle pipe(CreateFileW(pipeName.c_str(), GENERIC_READ | GENERIC_WRITE,
            0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr));
        if (!pipe) throw Win32Error("CreateFileW(native control pipe)");
        ULONG serverPid{};
        if (!GetNamedPipeServerProcessId(pipe.get(), &serverPid) ||
            serverPid != request->bridgePid)
            throw Win32Error("Native bridge server identity", ERROR_ACCESS_DENIED);

        if (request->inspectOnly) {
            std::wstring preparation;
            const auto prepared = workfor::taskbar::PrepareTaskbarCompatibility(preparation);
            if (FAILED(prepared)) {
                WriteFrame(pipe.get(), {{"protocolVersion", 1}, {"type", "native.error"},
                    {"code", "UNSUPPORTED_BUILD"}, {"hresult", prepared},
                    {"diagnostic", Utf8(preparation)}, {"layoutAttached", false}}, kHandshakeTimeoutMs);
                throw std::runtime_error("Taskbar module preparation failed");
            }
        }
        UniqueHandle server(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,
            FALSE, serverPid));
        if (!server || TokenSid(server.get()) != TokenSid(GetCurrentProcess()) ||
            IntegrityLevel(server.get()) != IntegrityLevel(GetCurrentProcess()) ||
            !IsNativeX64(server.get()) ||
            ProcessSession(serverPid) != ProcessSession(GetCurrentProcessId()) ||
            _wcsicmp(ProcessImagePath(server.get()).c_str(), ExpectedBridgeImage().c_str()) != 0)
            throw Win32Error("Native bridge server identity", ERROR_ACCESS_DENIED);

        WriteFrame(pipe.get(), {
            {"protocolVersion", 1}, {"type", "native.hello"},
            {"nativeAbiVersion", kNativeAbiVersion}, {"moduleVersion", kNativeVersion},
            {"processId", GetCurrentProcessId()},
            {"processCreationTime", std::to_string(ProcessCreationTime(GetCurrentProcess()))},
            {"targetThreadId", request->targetThreadId}, {"attempt", request->attempt},
            {"pinnedUntilProcessExit", true},
            {"capabilities", request->inspectOnly
                ? nlohmann::json{"readOnlyExplorerInspection", "logicalDetach"}
                : nlohmann::json{"probeBootstrap", "logicalDetach"}}
        }, kHandshakeTimeoutMs);
        const auto accept = ReadFrame(pipe.get(), kHandshakeTimeoutMs);
        if (accept.value("protocolVersion", 0) != 1 ||
            accept.value("type", "") != "native.accept" ||
            accept.value("nativeAbiVersion", 0) != 1 ||
            !accept.contains("generation") || !accept["generation"].is_string())
            throw Win32Error("Invalid native.accept", ERROR_INVALID_DATA);
        const auto generation = accept["generation"].get<std::string>();
        if (generation.empty() || generation.size() > 128)
            throw Win32Error("Invalid generation", ERROR_INVALID_DATA);

        double widthDip = 260.0;
        std::uint64_t configRevision{};
        const auto state = [&](const char* value, const std::string& requestId = std::string{},
            const std::string& code = std::string{}) {
            WriteFrame(pipe.get(), {{"protocolVersion", 1}, {"type", "native.state"},
                {"generation", generation}, {"state", value},
                {"layoutAttached", g_attached.load()}, {"displayUpdates", updateCount.load()},
                {"displayConnections", g_displayConnections.load()},
                {"clickFramesSent", g_clickFramesSent.load()},
                {"widthDip", widthDip}, {"configRevision", configRevision},
                {"requestId", requestId}, {"code", code},
                {"targetProcessId", GetCurrentProcessId()},
                {"targetCreationTime", std::to_string(ProcessCreationTime(GetCurrentProcess()))}}, kCommandTimeoutMs);
        };
        state("Bootstrapped");
        if (request->inspectOnly && accept.value("includeInspection", true)) {
            auto inspection = std::make_shared<UiCommand>(); inspection->kind = UiCommand::Kind::Inspect;
            const auto hr = InvokeUi(inspection);
            if (FAILED(hr)) throw std::runtime_error("ROOT_INSPECTION_FAILED");
            request->inspectionJson = inspection->inspectionJson;
            // Serialize non-ASCII as escapes before splitting; every fragment
            // is a valid UTF-8 JSON string even at a chunk boundary.
            if (request->inspectionJson.empty() || request->inspectionJson.size() > kMaxInspectionBytes)
                throw Win32Error("Inspection size limit", ERROR_INVALID_DATA);
            const auto parsed = nlohmann::json::parse(request->inspectionJson);
            const auto ascii = parsed.dump(-1, ' ', true);
            if (ascii.size() > kMaxInspectionBytes)
                throw Win32Error("Inspection size limit", ERROR_INVALID_DATA);
            constexpr size_t chunkSize = 6'000;
            const auto chunks = (ascii.size() + chunkSize - 1) / chunkSize;
            for (size_t i = 0; i < chunks; ++i) {
                WriteFrame(pipe.get(), {{"protocolVersion", 1},
                    {"type", "native.inspection.chunk"}, {"generation", generation},
                    {"index", i}, {"count", chunks},
                    {"jsonPart", ascii.substr(i * chunkSize, chunkSize)}}, kCommandTimeoutMs);
            }
        }
        while (true) {
            const auto command = ReadFrame(pipe.get(), 30'000);
            if (command.value("protocolVersion", 0) != 1 ||
                command.value("generation", "") != generation)
                throw Win32Error("Control generation mismatch", ERROR_INVALID_DATA);
            const auto type = command.value("type", "");
            if (type == "native.detach") {
                stopDisplay.store(true);
                if (display.joinable()) display.join();
                auto ui = std::make_shared<UiCommand>(); ui->kind = UiCommand::Kind::Detach;
                const auto hr = request->liveCapable ? InvokeUi(ui) : S_OK;
                detached = SUCCEEDED(hr) && !g_attached.load();
                state(detached ? "Dormant" : "DetachPending");
                break;
            }
            if (type == "native.queryState" || type == "native.lease")
                state(g_attached.load() ? "Attached" : "Bootstrapped");
            else if (type == "native.attach") {
                if (!request->liveCapable) {
                    WriteFrame(pipe.get(), {{"protocolVersion", 1}, {"type", "native.error"},
                        {"generation", generation}, {"code", "LAYOUT_ADAPTER_NOT_CONNECTED"},
                        {"layoutAttached", false}}, kCommandTimeoutMs);
                    continue;
                }
                if (g_attached.load()) { state("Attached"); continue; }
                activeMarker.reset(OpenMutexW(SYNCHRONIZE, FALSE, ActiveMutexName().c_str()));
                if (!activeMarker) throw Win32Error("Active marker missing");
                auto ui = std::make_shared<UiCommand>(); ui->kind = UiCommand::Kind::Attach;
                ui->widthDip = command.value("widthDip", 260.0);
                widthDip = ui->widthDip;
                configRevision = command.value("configRevision", std::uint64_t{});
                detached = false; // A timed-out queued attach may still run.
                const auto hr = InvokeUi(ui);
                if (FAILED(hr)) {
                    WriteFrame(pipe.get(), {{"protocolVersion", 1}, {"type", "native.error"},
                        {"generation", generation}, {"code", "LAYOUT_ATTACH_FAILED"},
                        {"hresult", hr}, {"diagnostic", hr == HRESULT_FROM_WIN32(ERROR_TIMEOUT)
                            ? "UI_OPERATION_PENDING" : Utf8(ui->diagnostic)},
                        {"layoutAttached", g_attached.load()}}, kCommandTimeoutMs);
                } else {
                    stopDisplay.store(false);
                    display = std::thread(DisplayWorker, std::ref(stopDisplay), std::ref(updateCount));
                    state("Attached");
                }
            } else if (type == "native.configure") {
                const auto requestId = command.value("requestId", "");
                const auto requested = command.value("widthDip", 0.0);
                auto ui = std::make_shared<UiCommand>(); ui->kind = UiCommand::Kind::Configure;
                ui->widthDip = requested;
                const auto hr = request->liveCapable ? InvokeUi(ui) : E_ACCESSDENIED;
                if (SUCCEEDED(hr)) {
                    widthDip = requested;
                    configRevision = command.value("configRevision", std::uint64_t{});
                    state("Attached", requestId, ui->diagnostic == L"INSUFFICIENT_SPACE" ? "INSUFFICIENT_SPACE" : "CONFIGURED");
                } else {
                    WriteFrame(pipe.get(), {{"protocolVersion", 1}, {"type", "native.error"},
                        {"generation", generation}, {"requestId", requestId},
                        {"code", "NATIVE_CONFIGURATION_FAILED"}, {"hresult", hr},
                        {"layoutAttached", g_attached.load()}}, kCommandTimeoutMs);
                }
            } else throw Win32Error("Unknown control message", ERROR_INVALID_DATA);
        }
    } catch (...) {
        // No exception may unwind into Explorer. The common cleanup path below
        // must reverse any active UI before releasing its activity marker.
    }
    stopDisplay.store(true);
    if (display.joinable()) display.join();
    if (request->liveCapable && (!detached || g_attached.load())) {
        auto command = std::make_shared<UiCommand>(); command->kind = UiCommand::Kind::Detach;
        detached = SUCCEEDED(InvokeUi(command)) && !g_attached.load();
    }
    if (!detached) {
        // Leave the marker and guard alive when layout reversal is not proven.
        activeMarker.release();
    } else g_running.store(false, std::memory_order_release);
    return 0;
}
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) g_module = module;
    return TRUE;
}

std::uint32_t WINAPI WorkForGetNativeAbiVersion() { return kNativeAbiVersion; }

LRESULT CALLBACK WorkForBootstrapHook(int code, WPARAM wParam, LPARAM lParam) {
    if (code < 0) return CallNextHookEx(nullptr, code, wParam, lParam);
    bool ownsGuard = false;
    try {
        if (code == HC_ACTION && wParam == PM_REMOVE && lParam) {
            const auto& message = *reinterpret_cast<const MSG*>(lParam);
            const auto bootstrap = RegisterWindowMessageW(kBootstrapMessage);
            std::array<wchar_t, 256> windowClass{};
            if (bootstrap && message.message == bootstrap && message.hwnd &&
                GetClassNameW(message.hwnd, windowClass.data(), static_cast<int>(windowClass.size())) &&
                message.wParam && message.lParam) {
                const bool probe = wcscmp(windowClass.data(), kProbeWindowClass) == 0 && IsProbeProcess();
                const bool inspectOnly = wcscmp(windowClass.data(), L"Shell_TrayWnd") == 0 && IsSystemExplorer();
                if (!probe && !inspectOnly) return CallNextHookEx(nullptr, code, wParam, lParam);
                bool expected = false;
                if (g_running.compare_exchange_strong(expected, true)) {
                    ownsGuard = true;
                    HMODULE pinned{};
                    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                        GET_MODULE_HANDLE_EX_FLAG_PIN,
                        reinterpret_cast<LPCWSTR>(&WorkForBootstrapHook), &pinned)) {
                        g_running.store(false);
                    } else {
                        auto request = std::make_unique<BootstrapRequest>();
                        request->bridgePid = static_cast<DWORD>(message.wParam);
                        request->attempt = static_cast<std::uint32_t>(message.lParam);
                        request->targetThreadId = GetCurrentThreadId();
                        request->inspectOnly = inspectOnly;
                        std::array<wchar_t, MAX_PATH> moduleName{};
                        GetModuleFileNameW(g_module, moduleName.data(), static_cast<DWORD>(moduleName.size()));
                        request->liveCapable = inspectOnly &&
                            _wcsicmp(std::filesystem::path(moduleName.data()).filename().c_str(), L"WorkFor.Taskbar.Live.dll") == 0;
                        if (inspectOnly) {
                            const auto current = g_controlWindow.load();
                            DWORD currentOwnerProcess{};
                            const auto currentOwnerThread = current && IsWindow(current)
                                ? GetWindowThreadProcessId(current, &currentOwnerProcess) : 0;
                            if (!current || !currentOwnerThread ||
                                currentOwnerProcess != GetCurrentProcessId() ||
                                currentOwnerThread != GetCurrentThreadId())
                                g_controlWindow.store(CreateControlWindow(message.hwnd));
                            else SetWindowLongPtrW(current, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(message.hwnd));
                        }
                        UniqueHandle worker(CreateThread(nullptr, 0, ControlWorker,
                            request.get(), 0, nullptr));
                        if (worker) { request.release(); ownsGuard = false; }
                        else g_running.store(false);
                    }
                }
            }
        }
    } catch (...) {
        if (ownsGuard) g_running.store(false);
        /* Hook chain must remain callable on all failure paths. */
    }
    return CallNextHookEx(nullptr, code, wParam, lParam);
}
