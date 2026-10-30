#pragma once

#include <Windows.h>
#include <sddl.h>
#include <bcrypt.h>
#include <nlohmann/json.hpp>
#include <algorithm>
#include <array>
#include <cstdint>
#include <filesystem>
#include <iomanip>
#include <limits>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>
#include "NativeProtocol.h"

namespace workfor::native {
class Win32Error : public std::runtime_error {
public:
    DWORD code;
    explicit Win32Error(const char* operation, DWORD value = GetLastError())
        : std::runtime_error(std::string(operation) + " failed: " +
            std::to_string(value)), code(value) {}
};

class UniqueHandle final {
public:
    UniqueHandle() noexcept = default;
    explicit UniqueHandle(HANDLE value) noexcept : value_(value) {}
    ~UniqueHandle() { reset(); }
    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;
    UniqueHandle(UniqueHandle&& other) noexcept : value_(other.release()) {}
    UniqueHandle& operator=(UniqueHandle&& other) noexcept {
        if (this != &other) reset(other.release());
        return *this;
    }
    HANDLE get() const noexcept { return value_; }
    explicit operator bool() const noexcept {
        return value_ && value_ != INVALID_HANDLE_VALUE;
    }
    HANDLE release() noexcept { HANDLE old = value_; value_ = nullptr; return old; }
    void reset(HANDLE value = nullptr) noexcept {
        if (*this) CloseHandle(value_);
        value_ = value;
    }
private:
    HANDLE value_ = nullptr;
};

inline std::string Utf8(const std::wstring& value) {
    if (value.empty()) return {};
    int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (count <= 0) throw Win32Error("WideCharToMultiByte");
    std::string result(static_cast<size_t>(count), '\0');
    if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), result.data(), count, nullptr, nullptr))
        throw Win32Error("WideCharToMultiByte");
    return result;
}

inline std::wstring Wide(const std::string& value) {
    if (value.empty()) return {};
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0);
    if (count <= 0) throw Win32Error("MultiByteToWideChar");
    std::wstring result(static_cast<size_t>(count), L'\0');
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), result.data(), count))
        throw Win32Error("MultiByteToWideChar");
    return result;
}


inline std::wstring ProcessImagePath(HANDLE process) {
    std::wstring path(32768, L'\0');
    DWORD size = static_cast<DWORD>(path.size());
    if (!QueryFullProcessImageNameW(process, 0, path.data(), &size))
        throw Win32Error("QueryFullProcessImageNameW");
    path.resize(size);
    return path;
}

inline std::uint64_t ProcessCreationTime(HANDLE process) {
    FILETIME creation{}, exit{}, kernel{}, user{};
    if (!GetProcessTimes(process, &creation, &exit, &kernel, &user))
        throw Win32Error("GetProcessTimes");
    return (static_cast<std::uint64_t>(creation.dwHighDateTime) << 32) |
        creation.dwLowDateTime;
}

inline std::wstring TokenSid(HANDLE process) {
    HANDLE rawToken{};
    if (!OpenProcessToken(process, TOKEN_QUERY, &rawToken))
        throw Win32Error("OpenProcessToken");
    UniqueHandle token(rawToken);
    DWORD size{};
    GetTokenInformation(token.get(), TokenUser, nullptr, 0, &size);
    if (!size) throw Win32Error("GetTokenInformation(size)");
    std::vector<BYTE> bytes(size);
    if (!GetTokenInformation(token.get(), TokenUser, bytes.data(), size, &size))
        throw Win32Error("GetTokenInformation(TokenUser)");
    LPWSTR sid{};
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(bytes.data())->User.Sid, &sid))
        throw Win32Error("ConvertSidToStringSidW");
    std::wstring result(sid);
    LocalFree(sid);
    return result;
}

inline DWORD IntegrityLevel(HANDLE process) {
    HANDLE rawToken{};
    if (!OpenProcessToken(process, TOKEN_QUERY, &rawToken))
        throw Win32Error("OpenProcessToken");
    UniqueHandle token(rawToken);
    DWORD size{};
    GetTokenInformation(token.get(), TokenIntegrityLevel, nullptr, 0, &size);
    if (!size) throw Win32Error("GetTokenInformation(integrity size)");
    std::vector<BYTE> bytes(size);
    if (!GetTokenInformation(token.get(), TokenIntegrityLevel, bytes.data(), size, &size))
        throw Win32Error("GetTokenInformation(TokenIntegrityLevel)");
    PSID sid = reinterpret_cast<TOKEN_MANDATORY_LABEL*>(bytes.data())->Label.Sid;
    return *GetSidSubAuthority(sid, *GetSidSubAuthorityCount(sid) - 1);
}

inline DWORD ProcessSession(DWORD pid) {
    DWORD session{};
    if (!ProcessIdToSessionId(pid, &session)) throw Win32Error("ProcessIdToSessionId");
    return session;
}

inline std::wstring ActiveMutexName() {
    return L"Local\\WorkFor.NativeActive." + TokenSid(GetCurrentProcess()) + L"." +
        std::to_wstring(ProcessSession(GetCurrentProcessId()));
}

inline bool IsNativeX64(HANDLE process) {
    USHORT processMachine{}, nativeMachine{};
    if (!IsWow64Process2(process, &processMachine, &nativeMachine))
        throw Win32Error("IsWow64Process2");
    return processMachine == IMAGE_FILE_MACHINE_UNKNOWN &&
        nativeMachine == IMAGE_FILE_MACHINE_AMD64;
}

class CurrentUserSecurity final {
public:
    CurrentUserSecurity() {
        std::wstring sddl = L"D:P(A;;GA;;;" + TokenSid(GetCurrentProcess()) + L")";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(),
            SDDL_REVISION_1, &descriptor_, nullptr))
            throw Win32Error("ConvertStringSecurityDescriptorToSecurityDescriptorW");
        attributes_.nLength = sizeof(attributes_);
        attributes_.lpSecurityDescriptor = descriptor_;
        attributes_.bInheritHandle = FALSE;
    }
    ~CurrentUserSecurity() { if (descriptor_) LocalFree(descriptor_); }
    CurrentUserSecurity(const CurrentUserSecurity&) = delete;
    CurrentUserSecurity& operator=(const CurrentUserSecurity&) = delete;
    SECURITY_ATTRIBUTES* get() noexcept { return &attributes_; }
private:
    PSECURITY_DESCRIPTOR descriptor_{};
    SECURITY_ATTRIBUTES attributes_{};
};

inline DWORD RemainingMs(ULONGLONG deadline) {
    const auto now = GetTickCount64();
    if (now >= deadline) throw Win32Error("IO deadline", ERROR_TIMEOUT);
    return static_cast<DWORD>(std::min<ULONGLONG>(deadline - now,
        std::numeric_limits<DWORD>::max()));
}

inline DWORD CompleteIo(HANDLE pipe, OVERLAPPED& operation, bool pending,
    DWORD transferred, ULONGLONG deadline) {
    if (!pending) return transferred;
    const auto now = GetTickCount64();
    const DWORD remaining = now >= deadline ? 0 :
        static_cast<DWORD>(std::min<ULONGLONG>(deadline - now, MAXDWORD));
    const DWORD wait = WaitForSingleObject(operation.hEvent, remaining);
    if (wait != WAIT_OBJECT_0) {
        const DWORD error = wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError();
        CancelIoEx(pipe, &operation);
        // OVERLAPPED and its event must outlive canceled IO completion.
        GetOverlappedResult(pipe, &operation, &transferred, TRUE);
        throw Win32Error("Overlapped IO", error);
    }
    if (!GetOverlappedResult(pipe, &operation, &transferred, FALSE))
        throw Win32Error("GetOverlappedResult");
    return transferred;
}

inline void TransferExact(HANDLE pipe, void* buffer, DWORD size, bool write,
    ULONGLONG deadline) {
    auto* bytes = static_cast<BYTE*>(buffer);
    DWORD offset{};
    while (offset < size) {
        UniqueHandle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!event) throw Win32Error("CreateEventW");
        OVERLAPPED operation{}; operation.hEvent = event.get();
        DWORD transferred{};
        RemainingMs(deadline);
        const BOOL completed = write
            ? WriteFile(pipe, bytes + offset, size - offset, &transferred, &operation)
            : ReadFile(pipe, bytes + offset, size - offset, &transferred, &operation);
        if (!completed && GetLastError() != ERROR_IO_PENDING)
            throw Win32Error(write ? "WriteFile" : "ReadFile");
        transferred = CompleteIo(pipe, operation, !completed, transferred, deadline);
        if (!transferred) throw Win32Error("Pipe closed", ERROR_BROKEN_PIPE);
        offset += transferred;
    }
}

inline void WriteFrame(HANDLE pipe, const nlohmann::json& message, DWORD timeoutMs) {
    const std::string text = message.dump();
    if (text.empty() || text.size() > kMaxFrameBytes)
        throw Win32Error("Frame size", ERROR_INVALID_DATA);
    const auto length = static_cast<std::uint32_t>(text.size());
    std::array<BYTE, 4> header{ static_cast<BYTE>(length),
        static_cast<BYTE>(length >> 8), static_cast<BYTE>(length >> 16),
        static_cast<BYTE>(length >> 24) };
    const auto deadline = GetTickCount64() + timeoutMs;
    TransferExact(pipe, header.data(), 4, true, deadline);
    TransferExact(pipe, const_cast<char*>(text.data()), length, true, deadline);
}

inline nlohmann::json ParseMessage(const std::string& text) {
    if (text.empty() || text.size() > kMaxFrameBytes)
        throw Win32Error("Frame size", ERROR_INVALID_DATA);
    // Reject duplicate properties and deep graphs before accepting the DOM.
    std::vector<std::vector<std::string>> keys;
    auto callback = [&keys](int depth, nlohmann::json::parse_event_t event,
        nlohmann::json& parsed) {
        if (depth > 16) throw Win32Error("JSON depth", ERROR_INVALID_DATA);
        if (event == nlohmann::json::parse_event_t::object_start) keys.emplace_back();
        if (event == nlohmann::json::parse_event_t::key) {
            auto key = parsed.get<std::string>();
            if (keys.empty() || std::find(keys.back().begin(), keys.back().end(), key) != keys.back().end())
                throw Win32Error("Duplicate JSON key", ERROR_INVALID_DATA);
            keys.back().push_back(std::move(key));
        }
        if (event == nlohmann::json::parse_event_t::object_end) keys.pop_back();
        return true;
    };
    auto result = nlohmann::json::parse(text, callback);
    if (!result.is_object()) throw Win32Error("JSON message must be object", ERROR_INVALID_DATA);
    return result;
}

inline nlohmann::json ReadFrame(HANDLE pipe, DWORD timeoutMs) {
    std::array<BYTE, 4> header{};
    const auto deadline = GetTickCount64() + timeoutMs;
    TransferExact(pipe, header.data(), 4, false, deadline);
    const std::uint32_t length = header[0] | (static_cast<std::uint32_t>(header[1]) << 8) |
        (static_cast<std::uint32_t>(header[2]) << 16) |
        (static_cast<std::uint32_t>(header[3]) << 24);
    if (!length || length > kMaxFrameBytes)
        throw Win32Error("Frame size", ERROR_INVALID_DATA);
    std::string text(length, '\0');
    TransferExact(pipe, text.data(), length, false, deadline);
    return ParseMessage(text);
}

inline std::string Sha256File(const std::filesystem::path& path) {
    UniqueHandle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ,
        nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
    if (!file) throw Win32Error("Open file for SHA256");
    BCRYPT_ALG_HANDLE algorithm{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0)
        throw std::runtime_error("BCryptOpenAlgorithmProvider failed");
    BCRYPT_HASH_HANDLE hash{};
    if (BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) < 0) {
        BCryptCloseAlgorithmProvider(algorithm, 0);
        throw std::runtime_error("BCryptCreateHash failed");
    }
    std::array<BYTE, 65536> buffer{};
    std::array<BYTE, 32> digest{};
    try {
        DWORD count{};
        do {
            if (!ReadFile(file.get(), buffer.data(), static_cast<DWORD>(buffer.size()), &count, nullptr))
                throw Win32Error("Read SHA256 input");
            if (count && BCryptHashData(hash, buffer.data(), count, 0) < 0)
                throw std::runtime_error("BCryptHashData failed");
        } while (count);
        if (BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) < 0)
            throw std::runtime_error("BCryptFinishHash failed");
    } catch (...) {
        BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0); throw;
    }
    BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0);
    std::ostringstream result;
    result << std::hex << std::setfill('0');
    for (const auto byte : digest) result << std::setw(2) << static_cast<int>(byte);
    return result.str();
}
}
