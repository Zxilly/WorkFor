#include "RuntimeControl.h"
#include <cmath>

namespace workfor::native {
RuntimeControl::RuntimeControl(double initialWidth, HANDLE input)
    : stopped_(CreateEventW(nullptr, TRUE, FALSE, nullptr)), input_(input) {
    if (!std::isfinite(initialWidth) || initialWidth < 180 || initialWidth > 360)
        throw std::invalid_argument("Width must be 180..360 DIP");
    if (!stopped_) throw Win32Error("CreateEventW(runtime control)");
    if (GetFileType(input_) != FILE_TYPE_PIPE)
        throw std::invalid_argument("--run-explorer requires a redirected stdin pipe");
    configuration_.widthDip = initialWidth;
    reader_ = std::thread(&RuntimeControl::Read, this);
}

RuntimeControl::~RuntimeControl() { Stop(); if (reader_.joinable()) reader_.join(); }
bool RuntimeControl::Stopping() const noexcept { return WaitForSingleObject(stopped_.get(), 0) == WAIT_OBJECT_0; }
bool RuntimeControl::Wait(DWORD milliseconds) const noexcept { return WaitForSingleObject(stopped_.get(), milliseconds) == WAIT_OBJECT_0; }
void RuntimeControl::Stop() noexcept { SetEvent(stopped_.get()); }
RuntimeConfiguration RuntimeControl::Configuration() const { std::lock_guard lock(gate_); return configuration_; }
std::vector<nlohmann::json> RuntimeControl::TakeNotifications() {
    std::lock_guard lock(gate_); std::vector<nlohmann::json> result; result.swap(notifications_); return result;
}
void RuntimeControl::Notify(std::string code, std::string requestId) {
    std::lock_guard lock(gate_);
    if (notifications_.size() == 8) notifications_.erase(notifications_.begin());
    notifications_.push_back({{"protocolVersion", 1}, {"type", "bridge.commandError"},
        {"requestId", std::move(requestId)}, {"payload", {{"code", std::move(code)}}}});
}

void RuntimeControl::Command(const nlohmann::json& message) {
    const auto requestId = message.value("requestId", "");
    if (requestId.size() > 128 || !message.contains("protocolVersion") ||
        !message["protocolVersion"].is_number_integer() || message["protocolVersion"] != 1) {
        Notify("NATIVE_PROTOCOL_INVALID"); return;
    }
    const auto type = message.value("type", "");
    if (type == "native.shutdown") { Stop(); return; }
    if (type != "native.configure" || requestId.empty() ||
        !message.contains("payload") || !message["payload"].is_object()) {
        Notify("NATIVE_COMMAND_INVALID", requestId); return;
    }
    const auto& payload = message["payload"];
    if (!payload.contains("widthDip") || !payload["widthDip"].is_number() ||
        !payload.contains("configRevision") || !payload["configRevision"].is_number_unsigned()) {
        Notify("NATIVE_CONFIGURATION_INVALID", requestId); return;
    }
    const auto width = payload["widthDip"].get<double>();
    if (!std::isfinite(width) || width < 180 || width > 360) {
        Notify("NATIVE_WIDTH_INVALID", requestId); return;
    }
    std::lock_guard lock(gate_);
    configuration_.widthDip = width;
    configuration_.configRevision = payload["configRevision"].get<std::uint64_t>();
    configuration_.requestId = requestId;
    ++configuration_.serial;
}

void RuntimeControl::Read() noexcept {
    std::vector<BYTE> pending;
    pending.reserve(kMaxFrameBytes + 4);
    try {
        while (!Stopping()) {
            DWORD available{};
            if (!PeekNamedPipe(input_, nullptr, 0, nullptr, &available, nullptr)) {
                if (GetLastError() != ERROR_BROKEN_PIPE) Notify("NATIVE_STDIN_ERROR");
                else if (!pending.empty()) Notify("NATIVE_STDIN_TRUNCATED");
                Stop(); return;
            }
            if (!available) { Wait(20); continue; }
            std::array<BYTE, 4096> buffer{};
            DWORD received{};
            if (!ReadFile(input_, buffer.data(), std::min<DWORD>(available,
                static_cast<DWORD>(buffer.size())), &received, nullptr) || !received) {
                if (!pending.empty()) Notify("NATIVE_STDIN_TRUNCATED");
                Stop(); return;
            }
            pending.insert(pending.end(), buffer.begin(), buffer.begin() + received);
            while (pending.size() >= 4 && !Stopping()) {
                const auto length = static_cast<std::uint32_t>(pending[0]) |
                    (static_cast<std::uint32_t>(pending[1]) << 8) |
                    (static_cast<std::uint32_t>(pending[2]) << 16) |
                    (static_cast<std::uint32_t>(pending[3]) << 24);
                if (!length || length > kMaxFrameBytes) {
                    Notify("NATIVE_FRAME_INVALID"); Stop(); return;
                }
                if (pending.size() < length + 4) break;
                const std::string text(reinterpret_cast<char*>(pending.data() + 4), length);
                pending.erase(pending.begin(), pending.begin() + length + 4);
                try { Command(ParseMessage(text)); }
                catch (...) { Notify("NATIVE_COMMAND_INVALID"); }
            }
        }
    } catch (...) { Notify("NATIVE_STDIN_ERROR"); Stop(); }
}

nlohmann::json RunRuntimeControlSelfTests() {
    HANDLE rawRead{}, rawWrite{};
    if (!CreatePipe(&rawRead, &rawWrite, nullptr, 4096)) throw Win32Error("CreatePipe(control self-test)");
    UniqueHandle input(rawRead), output(rawWrite);
    RuntimeControl control(200, input.get());
    auto send = [&](const nlohmann::json& message, bool fragments = false) {
        const auto text = message.dump(); const auto size = static_cast<std::uint32_t>(text.size());
        std::vector<BYTE> bytes{static_cast<BYTE>(size), static_cast<BYTE>(size >> 8),
            static_cast<BYTE>(size >> 16), static_cast<BYTE>(size >> 24)};
        bytes.insert(bytes.end(), text.begin(), text.end());
        DWORD written{};
        if (fragments) {
            for (const BYTE byte : bytes)
                if (!WriteFile(output.get(), &byte, 1, &written, nullptr) || written != 1)
                    throw Win32Error("Write control fragment");
        } else if (!WriteFile(output.get(), bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) || written != bytes.size())
            throw Win32Error("Write control frame");
    };
    send({{"protocolVersion", 1}, {"type", "native.configure"}, {"requestId", "old"},
        {"payload", {{"widthDip", 180}, {"configRevision", 1U}}}}, true);
    send({{"protocolVersion", 1}, {"type", "native.configure"}, {"requestId", "latest"},
        {"payload", {{"widthDip", 360}, {"configRevision", 2U}}}});
    const auto deadline = GetTickCount64() + 1'000;
    while (control.Configuration().serial < 2 && GetTickCount64() < deadline) Sleep(5);
    auto config = control.Configuration();
    if (config.serial != 2 || config.widthDip != 360 || config.requestId != "latest")
        throw std::runtime_error("Latest bounded native configuration was not applied");
    send({{"protocolVersion", 1}, {"type", "native.configure"}, {"requestId", "invalid"},
        {"payload", {{"widthDip", 179}, {"configRevision", 3U}}}});
    send({{"protocolVersion", 1}, {"type", "native.shutdown"}, {"requestId", "stop"}});
    if (!control.Wait(1'000)) throw std::runtime_error("Shutdown command did not cancel input reader");
    const auto notifications = control.TakeNotifications();
    if (notifications.size() != 1 || notifications[0]["payload"]["code"] != "NATIVE_WIDTH_INVALID")
        throw std::runtime_error("Invalid width was not rejected");
    output.reset();
    HANDLE eofRead{}, eofWrite{};
    if (!CreatePipe(&eofRead, &eofWrite, nullptr, 4096)) throw Win32Error("CreatePipe(EOF self-test)");
    UniqueHandle eofInput(eofRead), eofOutput(eofWrite);
    RuntimeControl eof(260, eofInput.get());
    eofOutput.reset();
    if (!eof.Wait(1'000)) throw std::runtime_error("Stdin EOF did not cancel controller");
    auto rejectedFrame = [](const std::vector<BYTE>& bytes, const char* expected) {
        HANDLE rawInput{}, rawOutput{};
        if (!CreatePipe(&rawInput, &rawOutput, nullptr, 4096)) throw Win32Error("CreatePipe(bad input)");
        UniqueHandle in(rawInput), out(rawOutput);
        RuntimeControl instance(260, in.get());
        DWORD written{};
        if (!WriteFile(out.get(), bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr))
            throw Win32Error("Write malformed input");
        out.reset();
        if (!instance.Wait(1'000)) throw std::runtime_error("Malformed stdin did not finish");
        const auto notices = instance.TakeNotifications();
        if (notices.empty() || notices[0]["payload"]["code"] != expected)
            throw std::runtime_error("Malformed stdin code mismatch");
    };
    rejectedFrame({1, 0}, "NATIVE_STDIN_TRUNCATED");
    rejectedFrame({1, 0x40, 0, 0}, "NATIVE_FRAME_INVALID");
    const auto began = GetTickCount64();
    {
        HANDLE cancelRead{}, cancelWrite{};
        if (!CreatePipe(&cancelRead, &cancelWrite, nullptr, 4096)) throw Win32Error("CreatePipe(cancel)");
        UniqueHandle cancelInput(cancelRead), heldWriter(cancelWrite);
        RuntimeControl canceled(260, cancelInput.get());
        canceled.Stop();
    }
    if (GetTickCount64() - began > 500)
        throw std::runtime_error("Cancel waited for stdin EOF instead of stopping promptly");
    return {{"framedConfigure", true}, {"fragmentedConfigure", true},
        {"latestValueMerged", true}, {"invalidWidthRejected", true},
        {"shutdownCancelsReader", true}, {"stdinEofCancelsReader", true},
        {"partialFrameEofRejected", true}, {"oversizedFrameRejected", true},
        {"cancelWithoutEofIsBounded", true}};
}
}
