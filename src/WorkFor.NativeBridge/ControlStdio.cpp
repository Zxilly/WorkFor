#include "DiscoveryWatcher.h"
#include <optional>

namespace workfor::native {
namespace {
bool ReadSyncExact(HANDLE input, BYTE* bytes, DWORD count, bool allowCleanEof) {
    DWORD offset{};
    while (offset < count) {
        DWORD read{};
        if (!ReadFile(input, bytes + offset, count - offset, &read, nullptr)) {
            if (allowCleanEof && !offset && GetLastError() == ERROR_BROKEN_PIPE) return false;
            throw Win32Error("Control stdin read");
        }
        if (!read) {
            if (allowCleanEof && !offset) return false;
            throw Win32Error("Truncated control stdin", ERROR_INVALID_DATA);
        }
        offset += read;
    }
    return true;
}

std::optional<nlohmann::json> ReadSyncFrame(HANDLE input) {
    std::array<BYTE, 4> header{};
    if (!ReadSyncExact(input, header.data(), 4, true)) return std::nullopt;
    const std::uint32_t length = header[0] |
        (static_cast<std::uint32_t>(header[1]) << 8) |
        (static_cast<std::uint32_t>(header[2]) << 16) |
        (static_cast<std::uint32_t>(header[3]) << 24);
    if (!length || length > kMaxFrameBytes)
        throw Win32Error("Control frame size", ERROR_INVALID_DATA);
    std::string text(length, '\0');
    ReadSyncExact(input, reinterpret_cast<BYTE*>(text.data()), length, false);
    return ParseMessage(text);
}

void WriteSyncFrame(HANDLE output, const nlohmann::json& response) {
    const auto text = response.dump();
    if (text.empty() || text.size() > kMaxFrameBytes)
        throw Win32Error("Control result frame size", ERROR_INVALID_DATA);
    const auto size = static_cast<std::uint32_t>(text.size());
    std::array<BYTE, 4> header{static_cast<BYTE>(size), static_cast<BYTE>(size >> 8),
        static_cast<BYTE>(size >> 16), static_cast<BYTE>(size >> 24)};
    const auto write = [&](const void* data, DWORD count) {
        DWORD offset{};
        while (offset < count) {
            DWORD written{};
            if (!WriteFile(output, static_cast<const BYTE*>(data) + offset,
                count - offset, &written, nullptr) || !written)
                throw Win32Error("Control stdout write");
            offset += written;
        }
    };
    write(header.data(), 4); write(text.data(), size);
}

nlohmann::json DiscoverState(bool enabled, std::uint64_t revision) {
    auto targets = nlohmann::json::array();
    for (const auto& target : DiscoverTaskbars()) {
        auto description = Describe(target);
        try {
            ValidateExplorerTarget(target);
            description["identityValid"] = true;
            description["code"] = "COMPATIBILITY_NOT_VERIFIED";
        } catch (const std::exception& error) {
            description["identityValid"] = false;
            description["code"] = "TARGET_IDENTITY_MISMATCH";
            description["message"] = error.what();
        }
        description["bootstrapAllowed"] = false;
        targets.push_back(std::move(description));
    }
    return {{"state", enabled ? "Unsupported" : "Dormant"},
        {"nativeAbiVersion", kNativeAbiVersion}, {"discoveryRevision", revision},
        {"code", enabled ? "COMPATIBILITY_NOT_VERIFIED" : "NOT_ATTACHED"},
        {"layoutAttached", false}, {"productionExplorerHooked", false},
        {"targets", targets}};
}
}

int RunControlStdio() {
    const auto input = GetStdHandle(STD_INPUT_HANDLE);
    const auto output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (GetFileType(input) != FILE_TYPE_PIPE || GetFileType(output) != FILE_TYPE_PIPE)
        throw Win32Error("--stdio requires inherited pipe handles", ERROR_INVALID_HANDLE);
    DiscoveryWatcher watcher;
    bool enabled = false;
    while (auto request = ReadSyncFrame(input)) {
        const auto requestId = request->value("requestId", "");
        nlohmann::json response{{"protocolVersion", 1}, {"requestId", requestId}};
        if (requestId.empty() || requestId.size() > 128 ||
            request->value("protocolVersion", 0) != 1) {
            response["type"] = "bridge.error";
            response["payload"] = {{"code", "PROTOCOL_INVALID"}};
        } else {
            const auto type = request->value("type", "");
            if (type == "bridge.ensureAttached" || type == "bridge.queryState" ||
                type == "bridge.detach") {
                if (type == "bridge.ensureAttached") enabled = true;
                if (type == "bridge.detach") enabled = false;
                response["type"] = "bridge.status";
                response["payload"] = DiscoverState(enabled, watcher.revision());
            } else {
                response["type"] = "bridge.error";
                response["payload"] = {{"code", "UNKNOWN_MESSAGE_TYPE"}};
            }
        }
        WriteSyncFrame(output, response);
    }
    return 0;
}
}
