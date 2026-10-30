#include "Compatibility.h"

#include <bcrypt.h>
#include <unknwn.h>
#include <array>
#include <cstring>
#include <iomanip>
#include <sstream>
#include <vector>
#include <winrt/Windows.UI.Core.h>

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "version.lib")

namespace workfor::taskbar {
namespace {

struct Handle {
    HANDLE value{INVALID_HANDLE_VALUE};
    ~Handle() { if (value != INVALID_HANDLE_VALUE && value) CloseHandle(value); }
};
struct HashAlgorithm {
    BCRYPT_ALG_HANDLE value{};
    ~HashAlgorithm() { if (value) BCryptCloseAlgorithmProvider(value, 0); }
};
struct Hash {
    BCRYPT_HASH_HANDLE value{};
    ~Hash() { if (value) BCryptDestroyHash(value); }
};

bool Readable(const void* pointer, SIZE_T length) noexcept {
    if (!pointer || !length) return false;
    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(pointer, &memory, sizeof(memory)) != sizeof(memory) ||
        memory.State != MEM_COMMIT || (memory.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return false;
    const auto start = reinterpret_cast<UINT_PTR>(pointer);
    const auto region = reinterpret_cast<UINT_PTR>(memory.BaseAddress);
    return start >= region && length <= memory.RegionSize &&
        start - region <= memory.RegionSize - length;
}

bool ReadPointer(const void* address, void*& value) noexcept {
    if (!Readable(address, sizeof(value))) return false;
    std::memcpy(&value, address, sizeof(value));
    return value != nullptr;
}

template<size_t N>
bool Matches(const BYTE* base, size_t offset, const std::array<BYTE, N>& expected) noexcept {
    return Readable(base + offset, N) && std::memcmp(base + offset, expected.data(), N) == 0;
}

// Independently recovered from the exact local file's Microsoft public PDB.
// See experiments/taskbar/research-symbols.py and research-notes.md.
constexpr size_t kSiteVtableRva = 0x1cbf50;
constexpr size_t kGetHostRva = 0x1097a0;
constexpr size_t kFrameHeightRva = 0x8aefc;
constexpr size_t kDecrefRva = 0x27ca0;
constexpr size_t kTaskBandSiteOffset = 0x50;
constexpr size_t kHostElementOffset = 0x10;
constexpr std::array<BYTE, 16> kGetHostBytes{
    0x40,0x53,0x48,0x83,0xec,0x20,0x48,0x8b,0x89,0xe8,0x01,0x00,0x00,0x48,0x8b,0xda};
constexpr std::array<BYTE, 16> kFrameHeightBytes{
    0x48,0x83,0xec,0x28,0x48,0x83,0xc1,0x10,0x48,0x83,0x39,0x00,0x74,0x0b,0xe8,0x15};
constexpr std::array<BYTE, 16> kDecrefBytes{
    0x40,0x53,0x48,0x83,0xec,0x20,0x48,0x8b,0xd9,0x83,0xc8,0xff,0xf0,0x0f,0xc1,0x41};

} // namespace

HRESULT FingerprintModuleFile(const std::wstring& path, ModuleFingerprint& result) noexcept {
    try {
        Handle file{CreateFileW(path.c_str(), GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr)};
        if (file.value == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
        HashAlgorithm algorithm;
        if (BCryptOpenAlgorithmProvider(&algorithm.value, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0)
            return E_FAIL;
        DWORD objectLength{}, returned{};
        if (BCryptGetProperty(algorithm.value, BCRYPT_OBJECT_LENGTH,
            reinterpret_cast<PUCHAR>(&objectLength), sizeof(objectLength), &returned, 0) < 0) return E_FAIL;
        std::vector<BYTE> storage(objectLength);
        Hash hash;
        if (BCryptCreateHash(algorithm.value, &hash.value, storage.data(), objectLength, nullptr, 0, 0) < 0)
            return E_FAIL;
        std::array<BYTE, 64 * 1024> buffer{};
        DWORD count{};
        for (;;) {
            if (!ReadFile(file.value, buffer.data(), static_cast<DWORD>(buffer.size()), &count, nullptr))
                return HRESULT_FROM_WIN32(GetLastError());
            if (!count) break;
            if (BCryptHashData(hash.value, buffer.data(), count, 0) < 0) return E_FAIL;
        }
        std::array<BYTE, 32> digest{};
        if (BCryptFinishHash(hash.value, digest.data(), static_cast<ULONG>(digest.size()), 0) < 0) return E_FAIL;
        std::ostringstream hex;
        hex << std::hex << std::setfill('0');
        for (auto value : digest) hex << std::setw(2) << static_cast<unsigned>(value);
        result.path = path;
        result.sha256 = hex.str();
        result.fileVersion.clear();
        DWORD ignored{};
        const auto bytes = GetFileVersionInfoSizeW(path.c_str(), &ignored);
        if (bytes) {
            std::vector<BYTE> version(bytes);
            VS_FIXEDFILEINFO* info{};
            UINT infoSize{};
            if (GetFileVersionInfoW(path.c_str(), 0, bytes, version.data()) &&
                VerQueryValueW(version.data(), L"\\", reinterpret_cast<void**>(&info), &infoSize) &&
                info && infoSize >= sizeof(*info)) {
                result.fileVersion = std::to_wstring(HIWORD(info->dwFileVersionMS)) + L"." +
                    std::to_wstring(LOWORD(info->dwFileVersionMS)) + L"." +
                    std::to_wstring(HIWORD(info->dwFileVersionLS)) + L"." +
                    std::to_wstring(LOWORD(info->dwFileVersionLS));
            }
        }
        return S_OK;
    } catch (...) { return winrt::to_hresult(); }
}

HRESULT ResolveCurrentTaskbarRoot(HWND taskbarWindow,
    winrt::Windows::UI::Xaml::FrameworkElement& root, std::wstring& diagnostic) noexcept {
    root = nullptr;
    try {
#ifndef _M_X64
        diagnostic = L"UNSUPPORTED_ARCHITECTURE";
        return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
#else
        DWORD process{};
        const auto thread = GetWindowThreadProcessId(taskbarWindow, &process);
        if (process != GetCurrentProcessId() || thread != GetCurrentThreadId()) {
            diagnostic = L"TARGET_OR_THREAD_MISMATCH";
            return RPC_E_WRONG_THREAD;
        }
        wchar_t className[64]{};
        if (!GetClassNameW(taskbarWindow, className, ARRAYSIZE(className)) ||
            std::wcscmp(className, L"Shell_TrayWnd") != 0) {
            diagnostic = L"PRIMARY_TASKBAR_REQUIRED";
            return E_INVALIDARG;
        }
        const auto module = GetModuleHandleW(L"taskbar.dll");
        if (!module) { diagnostic = L"TASKBAR_MODULE_NOT_LOADED"; return HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND); }
        std::array<wchar_t, 32768> path{};
        if (!GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size())))
            return HRESULT_FROM_WIN32(GetLastError());
        ModuleFingerprint fingerprint;
        auto hr = FingerprintModuleFile(path.data(), fingerprint);
        if (FAILED(hr)) { diagnostic = L"MODULE_FINGERPRINT_FAILED"; return hr; }
        if (fingerprint.sha256 != kResearchTaskbarSha256) {
            diagnostic = L"UNSUPPORTED_BUILD: exact research fingerprint is required";
            return HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
        }
        const auto base = reinterpret_cast<const BYTE*>(module);
        if (!Matches(base, kGetHostRva, kGetHostBytes) || !Matches(base, kFrameHeightRva, kFrameHeightBytes) ||
            !Matches(base, kDecrefRva, kDecrefBytes)) {
            diagnostic = L"IN_MEMORY_SYMBOL_BYTES_MISMATCH";
            return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        }
        const auto bandWindow = reinterpret_cast<HWND>(GetPropW(taskbarWindow, L"TaskbandHWND"));
        if (!bandWindow) { diagnostic = L"TASKBAND_WINDOW_NOT_FOUND"; return E_FAIL; }
        auto object = reinterpret_cast<BYTE*>(GetWindowLongPtrW(bandWindow, 0));
        void* vtable{};
        if (!object || !ReadPointer(object + kTaskBandSiteOffset, vtable) ||
            vtable != base + kSiteVtableRva) {
            diagnostic = L"TASKBAND_SITE_LAYOUT_MISMATCH";
            return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        }
        struct HostReference { void* object{}; void* control{}; } host;
        using GetHost = void* (__fastcall*)(void*, void*);
        using Decref = void (__fastcall*)(void*);
        const auto getHost = reinterpret_cast<GetHost>(const_cast<BYTE*>(base) + kGetHostRva);
        const auto decref = reinterpret_cast<Decref>(const_cast<BYTE*>(base) + kDecrefRva);
        getHost(object + kTaskBandSiteOffset, &host);
        struct ReleaseHost {
            HostReference& host;
            Decref release;
            ~ReleaseHost() { if (host.control) release(host.control); }
        } releaseHost{host, decref};
        void* inspectable{};
        if (!host.object || !host.control ||
            !ReadPointer(static_cast<BYTE*>(host.object) + kHostElementOffset, inspectable) ||
            !Readable(inspectable, sizeof(void*))) {
            diagnostic = L"TASKBAR_HOST_ELEMENT_NOT_FOUND";
            return E_FAIL;
        }
        winrt::Windows::UI::Xaml::FrameworkElement frame{nullptr};
        hr = static_cast<IUnknown*>(inspectable)->QueryInterface(
            winrt::guid_of<winrt::Windows::UI::Xaml::FrameworkElement>(), winrt::put_abi(frame));
        if (FAILED(hr) || !frame) { diagnostic = L"HOST_IS_NOT_FRAMEWORK_ELEMENT"; return FAILED(hr) ? hr : E_FAIL; }
        if (!frame.Dispatcher().HasThreadAccess()) { diagnostic = L"XAML_OWNER_THREAD_MISMATCH"; return RPC_E_WRONG_THREAD; }
        const auto xamlRoot = frame.XamlRoot();
        root = xamlRoot ? xamlRoot.Content().try_as<winrt::Windows::UI::Xaml::FrameworkElement>() : nullptr;
        if (!root) { diagnostic = L"XAML_ROOT_NOT_FOUND"; return E_FAIL; }
        diagnostic = L"EXACT_RESEARCH_ROOT_RESOLVED; layout compatibility still requires validation";
        return S_OK;
#endif
    } catch (...) {
        diagnostic = L"ROOT_RESOLUTION_EXCEPTION";
        return winrt::to_hresult();
    }
}

} // namespace workfor::taskbar
