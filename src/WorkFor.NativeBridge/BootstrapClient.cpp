#include "BootstrapClient.h"
#include "ProtocolSelfTest.h"
#include "RuntimeControl.h"
#include "../WorkFor.Taskbar/Compatibility.h"
#include <TlHelp32.h>
#include <thread>
#include <functional>
#include <iostream>
#include <deque>

namespace workfor::native {
namespace {
class OwnedProbe final {
public:
    UniqueHandle process;
    UniqueHandle thread;
    DWORD pid{};
    HWND window{};
    ~OwnedProbe() {
        if (!process) return;
        if (window) PostMessageW(window, WM_CLOSE, 0, 0);
        if (WaitForSingleObject(process.get(), 3'000) == WAIT_TIMEOUT) {
            // Only the child process created and owned by this experiment is
            // terminated. There is no Explorer termination path.
            TerminateProcess(process.get(), ERROR_PROCESS_ABORTED);
            WaitForSingleObject(process.get(), 3'000);
        }
    }
};

class Hook final {
public:
    explicit Hook(HHOOK hook) : hook_(hook) {}
    ~Hook() { if (hook_) UnhookWindowsHookEx(hook_); }
    void reset() {
        if (hook_) {
            if (!UnhookWindowsHookEx(hook_)) throw Win32Error("UnhookWindowsHookEx");
            hook_ = nullptr;
        }
    }
private:
    HHOOK hook_{};
};

LRESULT CALLBACK ProbeWindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    if (message == WM_CLOSE) { DestroyWindow(window); return 0; }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wParam, lParam);
}

std::uint32_t RandomAttempt() {
    std::uint32_t value{};
    if (BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&value), sizeof(value),
        BCRYPT_USE_SYSTEM_PREFERRED_RNG) < 0)
        throw std::runtime_error("BCryptGenRandom failed");
    return value ? value : 1;
}

HWND FindOwnedProbeWindow(DWORD pid) {
    struct Search { DWORD pid; HWND result{}; } search{pid};
    EnumWindows([](HWND window, LPARAM parameter) -> BOOL {
        auto& search = *reinterpret_cast<Search*>(parameter);
        DWORD windowPid{};
        GetWindowThreadProcessId(window, &windowPid);
        if (windowPid != search.pid) return TRUE;
        std::array<wchar_t, 256> name{};
        if (GetClassNameW(window, name.data(), static_cast<int>(name.size())) &&
            wcscmp(name.data(), kProbeWindowClass) == 0) {
            search.result = window;
            return FALSE;
        }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&search));
    return search.result;
}

void ConnectServer(HANDLE pipe) {
    UniqueHandle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!event) throw Win32Error("CreateEventW");
    OVERLAPPED operation{}; operation.hEvent = event.get();
    const BOOL completed = ConnectNamedPipe(pipe, &operation);
    if (completed) return;
    const auto error = GetLastError();
    if (error == ERROR_PIPE_CONNECTED) return;
    if (error != ERROR_IO_PENDING) throw Win32Error("ConnectNamedPipe", error);
    CompleteIo(pipe, operation, true, 0, GetTickCount64() + kHandshakeTimeoutMs);
}

nlohmann::json RunOneAttempt(const TargetIdentity& target, HOOKPROC hookEntry,
    HMODULE module, std::uint32_t attempt, bool inspectOnly = false,
    unsigned liveDurationSeconds = 0, RuntimeControl* control = nullptr,
    double initialWidth = 260.0, const std::function<void(const nlohmann::json&)>& progress = {}) {
    CurrentUserSecurity security;
    const auto pipeName = ProbePipeName(GetCurrentProcessId(), attempt);
    UniqueHandle pipe(CreateNamedPipeW(pipeName.c_str(), PIPE_ACCESS_DUPLEX |
        FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1, kMaxFrameBytes, kMaxFrameBytes, 0, security.get()));
    if (!pipe) throw Win32Error("CreateNamedPipeW");
    ValidateSameUserTarget(target);
    const auto hookHandle = SetWindowsHookExW(WH_GETMESSAGE, hookEntry, module,
        target.threadId);
    if (!hookHandle) throw Win32Error("SetWindowsHookExW");
    Hook hook(hookHandle);
    const auto message = RegisterWindowMessageW(kBootstrapMessage);
    if (!message || !PostMessageW(target.window, message,
        GetCurrentProcessId(), static_cast<LPARAM>(attempt)))
        throw Win32Error("PostMessageW(bootstrap)");
    ConnectServer(pipe.get());
    ULONG clientPid{};
    if (!GetNamedPipeClientProcessId(pipe.get(), &clientPid) || clientPid != target.processId)
        throw Win32Error("Named pipe target identity mismatch", ERROR_ACCESS_DENIED);
    const auto hello = ReadFrame(pipe.get(), kHandshakeTimeoutMs);
    if (hello.value("type", "") == "native.error")
        throw BridgeFailure(hello.value("code", "NATIVE_START_FAILED"), hello.value("diagnostic", "Native bootstrap preparation failed"));
    if (hello.value("protocolVersion", 0) != 1 || hello.value("type", "") != "native.hello" ||
        hello.value("nativeAbiVersion", 0) != 1 ||
        hello.value("processId", 0UL) != target.processId ||
        hello.value("targetThreadId", 0UL) != target.threadId ||
        hello.value("attempt", 0U) != attempt ||
        hello.value("processCreationTime", "") != std::to_string(target.creationTime) ||
        !hello.value("pinnedUntilProcessExit", false))
        throw Win32Error("native.hello mismatch", ERROR_INVALID_DATA);
    // Removing a hook does not prove all callbacks are finished. DLL hello is
    // accepted only after it confirms its independent PIN lifetime.
    hook.reset();
    const auto generation = std::to_string(GetCurrentProcessId()) + "-" +
        std::to_string(attempt);
    WriteFrame(pipe.get(), {{"protocolVersion", 1}, {"type", "native.accept"},
        {"nativeAbiVersion", 1}, {"generation", generation},
        {"includeInspection", control == nullptr}}, kCommandTimeoutMs);
    auto state = ReadFrame(pipe.get(), kCommandTimeoutMs);
    if (state.value("state", "") != "Bootstrapped" ||
        state.value("generation", "") != generation || state.value("layoutAttached", true))
        throw Win32Error("Expected truthful Bootstrapped state", ERROR_INVALID_DATA);

    nlohmann::json inspection;
    nlohmann::json unsupported;
    if (inspectOnly && !control) {
        std::string result;
        size_t expectedCount{};
        for (size_t index = 0;; ++index) {
            const auto chunk = ReadFrame(pipe.get(), kCommandTimeoutMs);
            if (chunk.value("protocolVersion", 0) != 1 ||
                chunk.value("type", "") != "native.inspection.chunk" ||
                chunk.value("generation", "") != generation ||
                chunk.value("index", SIZE_MAX) != index ||
                !chunk.contains("jsonPart") || !chunk["jsonPart"].is_string())
                throw Win32Error("Invalid inspection chunk", ERROR_INVALID_DATA);
            const auto count = chunk.value("count", size_t{});
            if (!count || count > 64 || (index && count != expectedCount))
                throw Win32Error("Invalid inspection chunk count", ERROR_INVALID_DATA);
            expectedCount = count;
            result += chunk["jsonPart"].get<std::string>();
            if (result.size() > kMaxInspectionBytes)
                throw Win32Error("Inspection size limit", ERROR_INVALID_DATA);
            if (index + 1 == count) break;
        }
        inspection = nlohmann::json::parse(result);
    } else if (!inspectOnly) {
        WriteFrame(pipe.get(), {{"protocolVersion", 1}, {"type", "native.attach"},
            {"generation", generation}}, kCommandTimeoutMs);
        unsupported = ReadFrame(pipe.get(), kCommandTimeoutMs);
        if (unsupported.value("code", "") != "LAYOUT_ADAPTER_NOT_CONNECTED" ||
            unsupported.value("layoutAttached", true))
            throw Win32Error("Unimplemented layout must be rejected", ERROR_INVALID_DATA);
    }

    nlohmann::json attached;
    auto leaseStates = nlohmann::json::array();
    if (liveDurationSeconds || control) {
        const auto initial = control ? control->Configuration() : RuntimeConfiguration{initialWidth};
        std::uint64_t appliedSerial = initial.serial;
        WriteFrame(pipe.get(), {{"protocolVersion", 1}, {"type", "native.attach"},
            {"generation", generation}, {"widthDip", initial.widthDip},
            {"configRevision", initial.configRevision}}, kCommandTimeoutMs);
        attached = ReadFrame(pipe.get(), kCommandTimeoutMs);
        if (attached.value("state", "") != "Attached" ||
            !attached.value("layoutAttached", false))
            throw BridgeFailure(attached.value("layoutAttached", false) ? "DETACH_PENDING" :
                (attached.value("diagnostic", "") == "UI_OPERATION_PENDING" ? "UI_OPERATION_PENDING" : "LAYOUT_ATTACH_FAILED"),
                "Live attach failed: " + attached.dump());
        if (!initial.requestId.empty()) attached["requestId"] = initial.requestId;
        if (progress) progress(attached);
        const auto endTime = GetTickCount64() + static_cast<ULONGLONG>(liveDurationSeconds) * 1'000;
        auto nextLease = GetTickCount64() + 1'000;
        while (control ? !control->Stopping() : GetTickCount64() < endTime) {
            if (control) {
                if (control->Wait(200)) break;
                for (const auto& notification : control->TakeNotifications())
                    std::cout << notification.dump() << std::endl;
                const auto configuration = control->Configuration();
                if (configuration.serial != appliedSerial) {
                    WriteFrame(pipe.get(), {{"protocolVersion", 1}, {"type", "native.configure"},
                        {"generation", generation}, {"widthDip", configuration.widthDip},
                        {"configRevision", configuration.configRevision},
                        {"requestId", configuration.requestId}}, kCommandTimeoutMs);
                    const auto configured = ReadFrame(pipe.get(), kCommandTimeoutMs);
                    if (progress) progress(configured);
                    appliedSerial = configuration.serial;
                }
            } else Sleep(1'000);
            if (WaitForSingleObject(target.process.get(), 0) != WAIT_TIMEOUT)
                throw BridgeFailure("EXPLORER_EXITED", "Target Explorer exited", target.processId, target.creationTime);
            DWORD currentPid{};
            if (!IsWindow(target.window) ||
                GetWindowThreadProcessId(target.window, &currentPid) != target.threadId ||
                currentPid != target.processId)
                throw BridgeFailure("TASKBAR_RECREATED", "Taskbar window identity changed", target.processId, target.creationTime);
            if (control && GetTickCount64() < nextLease) continue;
            nextLease = GetTickCount64() + 1'000;
            WriteFrame(pipe.get(), {{"protocolVersion", 1}, {"type", "native.lease"},
                {"generation", generation}}, kCommandTimeoutMs);
            auto current = ReadFrame(pipe.get(), kCommandTimeoutMs);
            if (current.value("state", "") != "Attached" ||
                !current.value("layoutAttached", false))
                throw BridgeFailure("CONTROL_CHANNEL_LOST", "Live lease failed: " + current.dump());
            if (progress) progress(current);
            // Continuous sessions must not retain an unbounded status history.
            if (leaseStates.size() == 32) leaseStates.erase(leaseStates.begin());
            leaseStates.push_back(std::move(current));
        }
    }

    WriteFrame(pipe.get(), {{"protocolVersion", 1}, {"type", "native.detach"},
        {"generation", generation}}, kCommandTimeoutMs);
    const auto dormant = ReadFrame(pipe.get(), kCommandTimeoutMs);
    if (dormant.value("state", "") != "Dormant" ||
        dormant.value("generation", "") != generation || dormant.value("layoutAttached", true))
        throw BridgeFailure("DETACH_PENDING", "Expected Dormant state: " + dormant.dump());
    if (progress) progress(dormant);
    auto report = nlohmann::json{{"attempt", attempt}, {"hello", hello},
        {"bootstrap", state}, {"detach", dormant}};
    if (inspectOnly) report["inspection"] = std::move(inspection);
    else report["layoutRejected"] = std::move(unsupported);
    if (liveDurationSeconds || control) {
        report["attach"] = std::move(attached);
        report["leases"] = std::move(leaseStates);
        report["durationSeconds"] = liveDurationSeconds;
    }
    return report;
}
}

int RunProbeTarget(const std::wstring& readyEventName) {
    if (readyEventName.rfind(L"Local\\WorkFor.ProbeReady.", 0) != 0)
        throw Win32Error("Invalid probe event", ERROR_INVALID_PARAMETER);
    UniqueHandle ready(OpenEventW(EVENT_MODIFY_STATE, FALSE, readyEventName.c_str()));
    if (!ready) throw Win32Error("OpenEventW(probe ready)");
    WNDCLASSW windowClass{};
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpfnWndProc = ProbeWindowProc;
    windowClass.lpszClassName = kProbeWindowClass;
    if (!RegisterClassW(&windowClass)) throw Win32Error("RegisterClassW(probe)");
    const auto window = CreateWindowExW(WS_EX_TOOLWINDOW, kProbeWindowClass,
        L"WorkFor bootstrap probe (hidden)", WS_OVERLAPPED,
        0, 0, 1, 1, nullptr, nullptr, windowClass.hInstance, nullptr);
    if (!window) throw Win32Error("CreateWindowExW(probe)");
    if (!SetEvent(ready.get())) throw Win32Error("SetEvent(probe ready)");
    MSG message{};
    BOOL result{};
    while ((result = GetMessageW(&message, nullptr, 0, 0)) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    if (result == -1) throw Win32Error("GetMessageW(probe)");
    return 0;
}

nlohmann::json RunBootstrapExperiment(const std::filesystem::path& imagePath) {
    const auto protocolTests = RunProtocolSelfTests();
    const auto runtimeControlTests = RunRuntimeControlSelfTests();
    if (!IsNativeX64(GetCurrentProcess()))
        throw Win32Error("Self-test requires native x64", ERROR_BAD_EXE_FORMAT);
    const auto modulePath = imagePath.parent_path() / L"WorkFor.Taskbar.dll";
    if (modulePath.filename() != L"WorkFor.Taskbar.dll")
        throw Win32Error("Unexpected module path", ERROR_INVALID_NAME);
    const auto attributes = GetFileAttributesW(modulePath.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT))
        throw Win32Error("Missing or redirected sibling DLL", ERROR_FILE_NOT_FOUND);
    const auto dllHash = Sha256File(modulePath);
    HMODULE module = LoadLibraryExW(modulePath.c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) throw Win32Error("LoadLibraryExW(probe DLL)");
    // The local DLL copy remains mapped until this short experiment exits. This
    // also avoids unloading its own loader-side hook callback while in flight.
    auto abi = reinterpret_cast<std::uint32_t(WINAPI*)()>(
        GetProcAddress(module, "WorkForGetNativeAbiVersion"));
    auto hook = reinterpret_cast<HOOKPROC>(GetProcAddress(module, "WorkForBootstrapHook"));
    if (!abi || !hook || abi() != kNativeAbiVersion)
        throw Win32Error("Native ABI mismatch", ERROR_REVISION_MISMATCH);

    const auto attempt = RandomAttempt();
    const std::wstring readyName = L"Local\\WorkFor.ProbeReady." +
        std::to_wstring(GetCurrentProcessId()) + L"." + std::to_wstring(attempt);
    CurrentUserSecurity security;
    UniqueHandle ready(CreateEventW(security.get(), TRUE, FALSE, readyName.c_str()));
    if (!ready || GetLastError() == ERROR_ALREADY_EXISTS)
        throw Win32Error("CreateEventW(probe ready)", ERROR_ALREADY_EXISTS);
    std::wstring command = L"\"" + imagePath.wstring() + L"\" --probe-target \"" + readyName + L"\"";
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION information{};
    if (!CreateProcessW(imagePath.c_str(), command.data(), nullptr, nullptr, FALSE,
        CREATE_NO_WINDOW, nullptr, imagePath.parent_path().c_str(), &startup, &information))
        throw Win32Error("CreateProcessW(owned probe)");
    OwnedProbe child;
    child.process.reset(information.hProcess); child.thread.reset(information.hThread);
    child.pid = information.dwProcessId;
    const auto childCreation = ProcessCreationTime(child.process.get());
    const HANDLE waits[] = {ready.get(), child.process.get()};
    if (WaitForMultipleObjects(2, waits, FALSE, kHandshakeTimeoutMs) != WAIT_OBJECT_0)
        throw Win32Error("Probe readiness deadline", ERROR_TIMEOUT);
    child.window = FindOwnedProbeWindow(child.pid);
    if (!child.window) throw Win32Error("Probe HWND missing", ERROR_INVALID_WINDOW_HANDLE);
    const auto target = InspectTarget(child.window);
    ValidateProbeTarget(target, child.pid, childCreation, imagePath);
    bool rejectedWrongIdentity = false;
    try { ValidateProbeTarget(target, child.pid + 1, childCreation, imagePath); }
    catch (const Win32Error&) { rejectedWrongIdentity = true; }
    if (!rejectedWrongIdentity) throw std::runtime_error("Wrong PID was not rejected");

    auto first = RunOneAttempt(target, hook, module, attempt);
    // The worker sets its dormant guard just after writing its final frame.
    // Retry the owned probe only after observing its control pipe disconnected.
    // This pause is a bounded test coordination wait, not a production polling loop.
    Sleep(100);
    auto second = RunOneAttempt(target, hook, module, attempt ^ 0x5a5a5a5aU);
    return {{"type", "probe.result"}, {"passed", true},
        {"productionExplorerHooked", false}, {"layoutAttached", false},
        {"dllSha256", dllHash}, {"target", Describe(target)},
        {"wrongIdentityRejected", rejectedWrongIdentity},
        {"protocolTests", protocolTests},
        {"runtimeControlTests", runtimeControlTests},
        {"attempts", {first, second}}};
}

nlohmann::json RunExplorerExperiment(const std::filesystem::path& imagePath,
    unsigned durationSeconds, RuntimeControl* control = nullptr,
    double initialWidth = 260.0, const std::function<void(const nlohmann::json&)>& progress = {}) {
    // Both research modes are explicit opt-in and file-exact gated. Duration 0
    // only inspects; a positive duration uses the separate fixed Live basename.
    auto targets = DiscoverTaskbars();
    const auto found = std::find_if(targets.begin(), targets.end(), [](const TargetIdentity& target) {
        return target.windowClass == L"Shell_TrayWnd";
    });
    if (found == targets.end()) throw Win32Error("Primary taskbar not found", ERROR_NOT_FOUND);
    const auto& target = *found;
    ValidateExplorerTarget(target);

    UniqueHandle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, target.processId));
    if (!snapshot) throw Win32Error("CreateToolhelp32Snapshot(Explorer modules)");
    MODULEENTRY32W entry{}; entry.dwSize = sizeof(entry);
    std::filesystem::path taskbarPath;
    if (!Module32FirstW(snapshot.get(), &entry)) throw Win32Error("Module32FirstW");
    do {
        if (_wcsicmp(entry.szModule, L"taskbar.dll") == 0) {
            taskbarPath = entry.szExePath;
            break;
        }
    } while (Module32NextW(snapshot.get(), &entry));
    if (taskbarPath.empty()) throw Win32Error("taskbar.dll not loaded", ERROR_MOD_NOT_FOUND);
    std::array<wchar_t, MAX_PATH> systemDirectory{};
    if (!GetSystemDirectoryW(systemDirectory.data(), static_cast<UINT>(systemDirectory.size())))
        throw Win32Error("GetSystemDirectoryW");
    const auto expectedPath = std::filesystem::path(systemDirectory.data()) / L"taskbar.dll";
    if (_wcsicmp(taskbarPath.c_str(), expectedPath.c_str()) != 0)
        throw Win32Error("Unexpected taskbar module path", ERROR_ACCESS_DENIED);
    const auto fingerprint = Sha256File(taskbarPath);
    if (fingerprint != workfor::taskbar::kResearchTaskbarSha256)
        throw Win32Error("UNSUPPORTED_BUILD: research taskbar fingerprint mismatch", ERROR_REVISION_MISMATCH);

    const auto modulePath = imagePath.parent_path() /
        ((durationSeconds || control) ? L"WorkFor.Taskbar.Live.dll" : L"WorkFor.Taskbar.dll");
    const auto attributes = GetFileAttributesW(modulePath.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT))
        throw Win32Error("Missing or redirected sibling DLL", ERROR_FILE_NOT_FOUND);
    const auto dllHash = Sha256File(modulePath);
    HMODULE module = LoadLibraryExW(modulePath.c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) throw Win32Error("LoadLibraryExW(inspect DLL)");
    auto abi = reinterpret_cast<std::uint32_t(WINAPI*)()>(
        GetProcAddress(module, "WorkForGetNativeAbiVersion"));
    auto hook = reinterpret_cast<HOOKPROC>(GetProcAddress(module, "WorkForBootstrapHook"));
    if (!abi || !hook || abi() != kNativeAbiVersion)
        throw Win32Error("Native ABI mismatch", ERROR_REVISION_MISMATCH);

    // Local DLL copy intentionally remains mapped until this bridge exits. The
    // Explorer copy is PINned and transitions to Dormant after its readonly dump.
    CurrentUserSecurity security;
    UniqueHandle activeMarker;
    if (durationSeconds || control) {
        activeMarker.reset(CreateMutexW(security.get(), FALSE, ActiveMutexName().c_str()));
        if (!activeMarker || GetLastError() == ERROR_ALREADY_EXISTS)
            throw Win32Error("Another active native module exists", ERROR_ALREADY_EXISTS);
    }
    nlohmann::json result;
    try {
        result = RunOneAttempt(target, hook, module, RandomAttempt(), true,
            durationSeconds, control, initialWidth, progress);
    } catch (...) {
        if (WaitForSingleObject(target.process.get(), 0) != WAIT_TIMEOUT)
            throw BridgeFailure("EXPLORER_EXITED", "Target Explorer exited during native session", target.processId, target.creationTime);
        if (!IsWindow(target.window))
            throw BridgeFailure("TASKBAR_RECREATED", "Target taskbar disappeared during native session", target.processId, target.creationTime);
        throw;
    }
    return {{"type", (durationSeconds || control) ? "explorer.liveExperiment" : "explorer.inspection"},
        {"readOnly", durationSeconds == 0 && !control},
        {"productionExplorerHooked", true}, {"layoutAttached", false},
        {"taskbarSha256", fingerprint}, {"dllSha256", dllHash},
        {"target", Describe(target)}, {"result", result}};
}

nlohmann::json RunExplorerInspection(const std::filesystem::path& imagePath) {
    return RunExplorerExperiment(imagePath, 0);
}

nlohmann::json RunExplorerLiveExperiment(const std::filesystem::path& imagePath, unsigned durationSeconds, double widthDip) {
    if (!durationSeconds || durationSeconds > 300)
        throw std::invalid_argument("Live experiment duration must be 1..300 seconds");
    return RunExplorerExperiment(imagePath, durationSeconds, nullptr, widthDip);
}

nlohmann::json RunExplorerContinuous(const std::filesystem::path& imagePath, double widthDip) {
    RuntimeControl control(widthDip);
    std::uint64_t retries{};
    std::deque<ULONGLONG> recoveryTimes;
    auto readinessDeadline = GetTickCount64() + 60'000;
    auto busyDeadline = GetTickCount64() + 10'000;
    auto emit = [](const nlohmann::json& state) {
        const auto requestId = state.value("requestId", "");
        const auto type = state.value("type", "") == "native.error" ? "bridge.commandError" :
            (requestId.empty() ? "bridge.liveState" : "bridge.configureResult");
        std::cout << nlohmann::json{{"type", type}, {"requestId", requestId},
            {"payload", state}}.dump() << std::endl;
    };
    while (!control.Stopping()) {
        try {
            return RunExplorerExperiment(imagePath, 0, &control, widthDip, emit);
        } catch (const BridgeFailure& failure) {
            if (failure.code != "EXPLORER_EXITED" && failure.code != "TASKBAR_RECREATED") throw;
            const auto now = GetTickCount64();
            while (!recoveryTimes.empty() && now - recoveryTimes.front() > 10 * 60'000) recoveryTimes.pop_front();
            recoveryTimes.push_back(now);
            if (recoveryTimes.size() > 3)
                throw BridgeFailure("RETRY_BUDGET_EXCEEDED", "Native recovery exceeded 3 events in 10 minutes");
            ++retries;
            readinessDeadline = now + 60'000;
            busyDeadline = now + 10'000;
            emit({{"state", "Reconnecting"}, {"code", failure.code},
                {"layoutAttached", false}, {"retryCount", retries},
                {"lastTargetProcessId", failure.targetProcessId},
                {"lastTargetCreationTime", std::to_string(failure.targetCreationTime)}});
        } catch (const Win32Error& failure) {
            if (failure.code != ERROR_NOT_FOUND && failure.code != ERROR_INVALID_WINDOW_HANDLE &&
                failure.code != ERROR_PROCESS_ABORTED && failure.code != ERROR_ALREADY_EXISTS) throw;
            const auto now = GetTickCount64();
            if (failure.code == ERROR_ALREADY_EXISTS && now >= busyDeadline)
                throw BridgeFailure("NATIVE_COMPONENT_BUSY", "An active component did not finish cleanup within 10 seconds");
            if (failure.code != ERROR_ALREADY_EXISTS && now >= readinessDeadline)
                throw BridgeFailure("EXPLORER_NOT_READY", "Explorer was not ready within 60 seconds");
            emit({{"state", "Reconnecting"}, {"code", failure.code == ERROR_ALREADY_EXISTS ? "NATIVE_COMPONENT_BUSY" : "EXPLORER_NOT_READY"},
                {"layoutAttached", false}, {"retryCount", retries}});
        }
        for (const auto& notification : control.TakeNotifications())
            std::cout << notification.dump() << std::endl;
        control.Wait(1'000);
    }
    for (const auto& notification : control.TakeNotifications())
        std::cout << notification.dump() << std::endl;
    return {{"result", {{"detach", {{"state", "Dormant"}, {"layoutAttached", false}}}}}};
}

std::string ClassifyNativeFailure(const std::exception& error) {
    if (const auto* failure = dynamic_cast<const BridgeFailure*>(&error)) return failure->code;
    if (const auto* win32 = dynamic_cast<const Win32Error*>(&error)) {
        switch (win32->code) {
        case ERROR_REVISION_MISMATCH:
            return std::string(error.what()).find("ABI") != std::string::npos ? "NATIVE_ABI_MISMATCH" : "UNSUPPORTED_BUILD";
        case ERROR_ACCESS_DENIED:
            return std::string(error.what()).find("IDENTITY") != std::string::npos ? "TARGET_IDENTITY_MISMATCH" : "LOAD_POLICY_BLOCKED";
        case ERROR_ALREADY_EXISTS: return "NATIVE_COMPONENT_BUSY";
        case ERROR_NOT_FOUND: return "EXPLORER_NOT_READY";
        case ERROR_PROCESS_ABORTED: return "EXPLORER_EXITED";
        case ERROR_TIMEOUT: return "NATIVE_HANDSHAKE_TIMEOUT";
        case ERROR_BROKEN_PIPE: return "CONTROL_CHANNEL_LOST";
        case ERROR_FILE_NOT_FOUND: return "NATIVE_BINARY_MISSING";
        case ERROR_INVALID_DATA: return "NATIVE_PROTOCOL_ERROR";
        default: return "NATIVE_START_FAILED";
        }
    }
    return "NATIVE_START_FAILED";
}
}
