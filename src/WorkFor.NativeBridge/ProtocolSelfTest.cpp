#include "ProtocolSelfTest.h"
#include <functional>
#include <atomic>
#include <thread>

namespace workfor::native {
namespace {
void Connect(HANDLE pipe) {
    UniqueHandle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!event) throw Win32Error("CreateEventW(protocol test)");
    OVERLAPPED operation{}; operation.hEvent = event.get();
    if (ConnectNamedPipe(pipe, &operation)) return;
    const auto error = GetLastError();
    if (error == ERROR_PIPE_CONNECTED) return;
    if (error != ERROR_IO_PENDING) throw Win32Error("ConnectNamedPipe(protocol test)", error);
    CompleteIo(pipe, operation, true, 0, GetTickCount64() + 5'000);
}

void WithPipe(const std::function<void(HANDLE)>& write,
    const std::function<void(HANDLE)>& read) {
    static std::atomic<unsigned> serial{0};
    const auto name = L"\\\\.\\pipe\\workfor-protocol-test-" +
        std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(serial++);
    CurrentUserSecurity security;
    UniqueHandle server(CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX |
        FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1, kMaxFrameBytes, kMaxFrameBytes, 0, security.get()));
    if (!server) throw Win32Error("CreateNamedPipeW(protocol test)");
    std::exception_ptr clientError;
    std::thread client([&] {
        try {
            UniqueHandle pipe(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE,
                0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr));
            if (!pipe) throw Win32Error("CreateFileW(protocol test)");
            write(pipe.get());
        } catch (...) { clientError = std::current_exception(); }
    });
    try { Connect(server.get()); read(server.get()); }
    catch (...) { client.join(); throw; }
    client.join();
    if (clientError) std::rethrow_exception(clientError);
}

void WriteText(HANDLE pipe, const std::string& text, bool fragmented = false) {
    const auto length = static_cast<std::uint32_t>(text.size());
    const std::array<BYTE, 4> header{static_cast<BYTE>(length),
        static_cast<BYTE>(length >> 8), static_cast<BYTE>(length >> 16),
        static_cast<BYTE>(length >> 24)};
    const auto deadline = GetTickCount64() + 5'000;
    if (!fragmented) {
        TransferExact(pipe, const_cast<BYTE*>(header.data()), 4, true, deadline);
        TransferExact(pipe, const_cast<char*>(text.data()), length, true, deadline);
        return;
    }
    for (const auto& value : header)
        TransferExact(pipe, const_cast<BYTE*>(&value), 1, true, deadline);
    for (const auto& value : text)
        TransferExact(pipe, const_cast<char*>(&value), 1, true, deadline);
}

bool RejectsText(const std::string& text) {
    bool rejected = false;
    WithPipe([&](HANDLE pipe) { WriteText(pipe, text); }, [&](HANDLE pipe) {
        try { static_cast<void>(ReadFrame(pipe, 1'000)); }
        catch (const std::exception&) { rejected = true; }
    });
    return rejected;
}
}

nlohmann::json RunProtocolSelfTests() {
    bool fragmented = false;
    WithPipe([](HANDLE pipe) {
        WriteText(pipe, R"({"type":"fragmented","nested":{"value":42}})", true);
    }, [&](HANDLE pipe) {
        const auto message = ReadFrame(pipe, 1'000);
        fragmented = message.at("nested").at("value") == 42;
    });
    bool oversized = false;
    WithPipe([](HANDLE pipe) {
        std::array<BYTE, 4> header{0x01, 0x40, 0x00, 0x00}; // 16385
        TransferExact(pipe, header.data(), 4, true, GetTickCount64() + 1'000);
    }, [&](HANDLE pipe) {
        try { static_cast<void>(ReadFrame(pipe, 1'000)); }
        catch (const Win32Error& error) { oversized = error.code == ERROR_INVALID_DATA; }
    });
    bool timeout = false;
    WithPipe([](HANDLE) { Sleep(120); }, [&](HANDLE pipe) {
        try { static_cast<void>(ReadFrame(pipe, 30)); }
        catch (const Win32Error& error) { timeout = error.code == ERROR_TIMEOUT; }
    });
    bool partialHeader = false;
    WithPipe([](HANDLE pipe) {
        BYTE byte = 1;
        TransferExact(pipe, &byte, 1, true, GetTickCount64() + 1'000);
    }, [&](HANDLE pipe) {
        try { static_cast<void>(ReadFrame(pipe, 1'000)); }
        catch (const Win32Error& error) { partialHeader = error.code == ERROR_BROKEN_PIPE; }
    });
    const bool duplicate = RejectsText(R"({"type":"first","type":"second"})");
    const bool malformed = RejectsText(R"({"type":})");
    const bool objectOnly = RejectsText(R"([1,2,3])");
    std::string deep = "{\"a\":";
    deep.append(18, '['); deep += "0"; deep.append(18, ']'); deep += "}";
    const bool depth = RejectsText(deep);
    nlohmann::json report{{"fragmentedFrame", fragmented},
        {"oversizedHeaderRejected", oversized}, {"readDeadlineEnforced", timeout},
        {"partialHeaderRejected", partialHeader}, {"duplicatePropertyRejected", duplicate},
        {"malformedJsonRejected", malformed}, {"nonObjectRejected", objectOnly},
        {"deepJsonRejected", depth}};
    for (const auto& value : report)
        if (value != true) throw std::runtime_error("Protocol self-test failed: " + report.dump());
    return report;
}
}
