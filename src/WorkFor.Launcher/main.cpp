#include <windows.h>
#include <shellapi.h>
#include <bcrypt.h>
#include <sddl.h>
#include <shlobj.h>
#include <tlhelp32.h>
#include <nlohmann/json.hpp>
#include <algorithm>
#include <array>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <optional>
#include <stdexcept>
#include <string>
#include <set>
#include <vector>

namespace fs = std::filesystem;
using json = nlohmann::json;

namespace {
class Handle {
public:
    explicit Handle(HANDLE value = nullptr) : value_(value) {}
    ~Handle() { if (value_ && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    HANDLE get() const { return value_; }
private:
    HANDLE value_;
};

std::wstring Wide(const std::string& value) {
    const int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (!size && !value.empty()) throw std::runtime_error("Invalid UTF-8 path.");
    std::wstring result(size, L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), size);
    return result;
}

std::wstring ImagePath(HANDLE process) {
    std::wstring value(32768, L'\0'); DWORD length = static_cast<DWORD>(value.size());
    if (!QueryFullProcessImageNameW(process, 0, value.data(), &length)) throw std::runtime_error("Cannot identify WorkFor process.");
    value.resize(length); return value;
}

fs::path ExecutableDirectory() { return fs::path(ImagePath(GetCurrentProcess())).parent_path(); }

std::wstring UserSid(HANDLE process = GetCurrentProcess()) {
    HANDLE tokenValue{};
    if (!OpenProcessToken(process, TOKEN_QUERY, &tokenValue)) throw std::runtime_error("Cannot open user token.");
    Handle token(tokenValue); DWORD length{};
    GetTokenInformation(token.get(), TokenUser, nullptr, 0, &length);
    std::vector<std::byte> buffer(length);
    if (!GetTokenInformation(token.get(), TokenUser, buffer.data(), length, &length)) throw std::runtime_error("Cannot read user token.");
    LPWSTR text{};
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &text)) throw std::runtime_error("Cannot read user SID.");
    std::wstring result(text); LocalFree(text); return result;
}

std::wstring Quote(const std::wstring& value) {
    std::wstring result = L"\""; size_t slashes{};
    for (const wchar_t c : value) {
        if (c == L'\\') { ++slashes; continue; }
        if (c == L'\"') result.append(slashes * 2 + 1, L'\\');
        else result.append(slashes, L'\\');
        result += c; slashes = 0;
    }
    result.append(slashes * 2, L'\\'); result += L'\"'; return result;
}

bool Within(const fs::path& child, const fs::path& root) {
    auto rootValue = fs::weakly_canonical(root).wstring();
    auto childValue = fs::weakly_canonical(child).wstring();
    if (rootValue.empty() || childValue.size() <= rootValue.size()) return false;
    if (rootValue.back() != L'\\') rootValue += L'\\';
    return childValue.size() > rootValue.size() && _wcsnicmp(childValue.c_str(), rootValue.c_str(), rootValue.size()) == 0;
}

bool SamePath(const fs::path& a, const fs::path& b) {
    return _wcsicmp(fs::weakly_canonical(a).c_str(),fs::weakly_canonical(b).c_str()) == 0;
}

fs::path Asset(const fs::path& root, const std::string& relative) {
    const fs::path path(Wide(relative));
    if (path.empty() || path.is_absolute() || path.has_root_name()) throw std::runtime_error("Package path must be relative.");
    for (const auto& part : path) if (part == L"..") throw std::runtime_error("Package path escapes its root.");
    const auto full = root / path;
    if (!fs::is_regular_file(full) || !Within(full, root)) throw std::runtime_error("Package file is missing or escapes its root.");
    return fs::weakly_canonical(full);
}

std::string Sha256(const fs::path& file) {
    BCRYPT_ALG_HANDLE algorithm{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) throw std::runtime_error("Cannot open SHA256.");
    struct CloseAlgorithm { BCRYPT_ALG_HANDLE value; ~CloseAlgorithm() { BCryptCloseAlgorithmProvider(value, 0); } } guard{algorithm};
    DWORD objectLength{}, read{};
    if (BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&objectLength), sizeof(objectLength), &read, 0) < 0)
        throw std::runtime_error("Cannot initialize SHA256.");
    std::vector<unsigned char> object(objectLength); BCRYPT_HASH_HANDLE hash{};
    if (BCryptCreateHash(algorithm, &hash, object.data(), objectLength, nullptr, 0, 0) < 0) throw std::runtime_error("Cannot create SHA256 hash.");
    struct DestroyHash { BCRYPT_HASH_HANDLE value; ~DestroyHash() { BCryptDestroyHash(value); } } hashGuard{hash};
    std::ifstream stream(file, std::ios::binary);
    if (!stream) throw std::runtime_error("Cannot read package asset.");
    std::array<unsigned char, 65536> buffer{};
    while (stream) {
        stream.read(reinterpret_cast<char*>(buffer.data()), buffer.size());
        if (const auto count = stream.gcount(); count && BCryptHashData(hash, buffer.data(), static_cast<ULONG>(count), 0) < 0)
            throw std::runtime_error("Cannot hash package asset.");
    }
    if (!stream.eof()) throw std::runtime_error("Cannot finish reading package asset.");
    std::array<unsigned char, 32> digest{};
    if (BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) < 0) throw std::runtime_error("Cannot finish SHA256.");
    constexpr char digits[] = "0123456789abcdef"; std::string text;
    for (auto byte : digest) { text += digits[byte >> 4]; text += digits[byte & 15]; }
    return text;
}

struct Package {
    fs::path root, host, script, ui, native;
    json manifest;
    explicit Package(fs::path directory, bool resolveExecutables = true) : root(std::move(directory)) {
        const auto manifestFile = root / L"release.json";
        if (!fs::is_regular_file(manifestFile) || fs::file_size(manifestFile) > 2 * 1024 * 1024) throw std::runtime_error("Missing or invalid release.json. Use the complete portable folder.");
        std::ifstream stream(manifestFile); manifest = json::parse(stream);
        if (manifest.at("schemaVersion") != 1) throw std::runtime_error("Unsupported release manifest.");
        if (!resolveExecutables) return;
        const auto& paths = manifest.at("paths");
        host = Asset(root, paths.at("host").get<std::string>());
        script = Asset(root, paths.at("script").get<std::string>());
        ui = Asset(root, paths.at("ui").get<std::string>());
        native = Asset(root, paths.at("native").get<std::string>());
        if (host.filename() != L"WorkFor.Host.exe" || script.filename() != L"WorkFor.ScriptHost.exe"
            || ui.filename() != L"WorkFor.UI.exe" || native.filename() != L"WorkFor.NativeBridge.exe")
            throw std::runtime_error("Release manifest executable roles are invalid.");
    }
    void Verify(bool all) const {
        const std::array<fs::path, 6> required{host,script,ui,native,native.parent_path()/L"WorkFor.Taskbar.dll",native.parent_path()/L"WorkFor.Taskbar.Live.dll"};
        std::array<bool, 6> covered{}; std::set<std::wstring> unique;
        if (!manifest.at("files").is_array() || manifest.at("files").size() > 20000) throw std::runtime_error("Invalid package inventory.");
        for (const auto& item : manifest.at("files")) {
            const auto path = Asset(root, item.at("path").get<std::string>());
            auto identity = path.wstring(); std::transform(identity.begin(),identity.end(),identity.begin(),::towlower);
            if (!unique.insert(identity).second) throw std::runtime_error("Duplicate package inventory path.");
            bool protectedFile{};
            for (size_t index = 0; index < required.size(); ++index) if (path == required[index]) { covered[index] = true; protectedFile = true; }
            if (!all && !protectedFile) continue;
            if (fs::file_size(path) != item.at("length").get<std::uintmax_t>() || Sha256(path) != item.at("sha256").get<std::string>())
                throw std::runtime_error("Package integrity check failed: " + item.at("path").get<std::string>());
        }
        if (std::any_of(covered.begin(),covered.end(),[](bool value){return !value;})) throw std::runtime_error("Release manifest does not cover required executables and native modules.");
    }
};

std::wstring PipeName(const std::wstring& sid, DWORD session) {
    return L"\\\\.\\pipe\\workfor-control-v1-" + sid + L"-" + std::to_wstring(session);
}

bool ExactIo(HANDLE pipe, void* buffer, DWORD length, bool writing, ULONGLONG deadline) {
    auto bytes = static_cast<unsigned char*>(buffer);
    DWORD offset{};
    while (offset < length) {
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!event.get()) throw std::runtime_error("Cannot create pipe event.");
        OVERLAPPED operation{}; operation.hEvent = event.get(); DWORD count{};
        const BOOL immediate = writing ? WriteFile(pipe, bytes + offset, length - offset, &count, &operation)
            : ReadFile(pipe, bytes + offset, length - offset, &count, &operation);
        if (!immediate) {
            if (GetLastError() != ERROR_IO_PENDING) return false;
            const ULONGLONG now = GetTickCount64();
            const DWORD remaining = now >= deadline ? 0 : static_cast<DWORD>(std::min<ULONGLONG>(deadline - now, MAXDWORD - 1));
            if (WaitForSingleObject(event.get(), remaining) != WAIT_OBJECT_0) {
                CancelIoEx(pipe, &operation);
                GetOverlappedResult(pipe, &operation, &count, TRUE);
                return false;
            }
            if (!GetOverlappedResult(pipe, &operation, &count, FALSE)) return false;
        }
        if (!count) return false;
        offset += count;
    }
    return true;
}

std::optional<DWORD> Control(const fs::path& root, const std::wstring& sid, DWORD session,
    const char* type, bool required, DWORD timeout = 2500, bool ignoreForeign = false) {
    const auto pipeName = PipeName(sid, session);
    if (!WaitNamedPipeW(pipeName.c_str(), timeout)) {
        if (!required && (GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_SEM_TIMEOUT)) return std::nullopt;
        throw std::runtime_error("WorkFor Host did not become available.");
    }
    Handle pipe(CreateFileW(pipeName.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr));
    if (pipe.get() == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot connect to WorkFor Host.");
    ULONG serverPid{};
    if (!GetNamedPipeServerProcessId(pipe.get(), &serverPid)) throw std::runtime_error("Cannot verify Host pipe server.");
    Handle server(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, serverPid));
    if (!server.get()) throw std::runtime_error("Cannot identify the Host pipe owner.");
    if (!Within(fs::path(ImagePath(server.get())), root)
        || fs::path(ImagePath(server.get())).filename() != L"WorkFor.Host.exe") {
        if (ignoreForeign) return std::nullopt;
        throw std::runtime_error("Another WorkFor installation owns this session. Quit that instance first.");
    }
    DWORD actualSession{};
    if (!ProcessIdToSessionId(serverPid, &actualSession) || actualSession != session) throw std::runtime_error("Host session mismatch.");
    const auto text = json{{"protocolVersion",1},{"type",type},{"requestId","launcher"},{"payload",json::object()}}.dump();
    DWORD length = static_cast<DWORD>(text.size()); const auto deadline = GetTickCount64() + timeout;
    if (!ExactIo(pipe.get(), &length, sizeof(length), true, deadline)
        || !ExactIo(pipe.get(), const_cast<char*>(text.data()), length, true, deadline)
        || !ExactIo(pipe.get(), &length, sizeof(length), false, deadline)
        || !length || length > 16384) throw std::runtime_error("Host control request failed.");
    std::string reply(length, '\0');
    if (!ExactIo(pipe.get(), reply.data(), length, false, deadline)) throw std::runtime_error("Host response timed out.");
    const auto result = json::parse(reply);
    if (result.at("protocolVersion") != 1 || result.at("requestId") != "launcher"
        || result.at("type").get<std::string>().ends_with(".error")) throw std::runtime_error("Host rejected launcher request.");
    return serverPid;
}

DWORD Start(const fs::path& executable, const std::vector<std::wstring>& arguments, bool wait = false) {
    std::wstring command = Quote(executable.wstring());
    for (const auto& argument : arguments) command += L" " + Quote(argument);
    STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
        nullptr, executable.parent_path().c_str(), &startup, &process)) throw std::runtime_error("Cannot start a packaged WorkFor executable.");
    Handle processHandle(process.hProcess), threadHandle(process.hThread);
    if (wait) {
        if (WaitForSingleObject(processHandle.get(), 10000) != WAIT_OBJECT_0) throw std::runtime_error("WorkFor command did not exit in time.");
        DWORD code{}; if (!GetExitCodeProcess(processHandle.get(), &code) || code != 0) throw std::runtime_error("WorkFor executable returned an error.");
    }
    return process.dwProcessId;
}

constexpr char InstallMarker[] = "WorkFor.Installation.v1:78b231eb-09b3-4c9e-a8af-62f4b9abeb84";

void ValidateInstallationRoot(const fs::path& root) {
    PWSTR folder{};
    if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &folder))) throw std::runtime_error("Cannot identify the per-user installation root.");
    const fs::path localData(folder); CoTaskMemFree(folder);
    const fs::path programs = localData / L"Programs";
    const fs::path expected = programs / L"WorkFor";
    for (const auto& part : {programs, expected}) {
        const auto attributes = GetFileAttributesW(part.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT))
            throw std::runtime_error("Installation root is absent or is a reparse point; refusing removal.");
    }
    if (!SamePath(root,expected) || root == root.root_path())
        throw std::runtime_error("Removal is allowed only from the fixed per-user WorkFor installation directory.");
    const auto marker = root / L".workfor-install";
    if (!fs::is_regular_file(marker) || fs::file_size(marker) > 256) throw std::runtime_error("Missing installation ownership marker.");
    std::ifstream stream(marker); std::string contents((std::istreambuf_iterator<char>(stream)), {});
    while (!contents.empty() && (contents.back() == '\r' || contents.back() == '\n')) contents.pop_back();
    if (contents != InstallMarker) throw std::runtime_error("Installation ownership marker does not match WorkFor.");
}

void CloseOwnedUi(const fs::path& root, const std::wstring& sid) {
    DWORD currentSession{};
    if (!ProcessIdToSessionId(GetCurrentProcessId(),&currentSession)) throw std::runtime_error("Cannot identify UI session.");
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    if (snapshot.get() == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot inspect running WorkFor UI.");
    PROCESSENTRY32W entry{sizeof(entry)};
    for (BOOL found = Process32FirstW(snapshot.get(), &entry); found; found = Process32NextW(snapshot.get(), &entry)) {
        if (_wcsicmp(entry.szExeFile, L"WorkFor.UI.exe") != 0) continue;
        Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID));
        if (!process.get()) continue;
        try {
            DWORD candidateSession{};
            if (!ProcessIdToSessionId(entry.th32ProcessID,&candidateSession) || candidateSession!=currentSession) continue;
            const fs::path image(ImagePath(process.get()));
            if (Within(image, root) && UserSid(process.get()) == sid) Start(image, {L"--quit"}, true);
        }
        catch (const std::exception&) { throw std::runtime_error("Cannot close the installed WorkFor UI; close it manually and retry."); }
    }
}

void RejectOtherSessionProcesses(const fs::path& root,const std::wstring& sid,DWORD session) {
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0));
    if (snapshot.get()==INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot inspect running installation processes.");
    PROCESSENTRY32W entry{sizeof(entry)};
    for (BOOL item=Process32FirstW(snapshot.get(),&entry);item;item=Process32NextW(snapshot.get(),&entry)) {
        if (_wcsicmp(entry.szExeFile,L"WorkFor.Host.exe") && _wcsicmp(entry.szExeFile,L"WorkFor.UI.exe")
            && _wcsicmp(entry.szExeFile,L"WorkFor.NativeBridge.exe")) continue;
        DWORD otherSession{};
        if (!ProcessIdToSessionId(entry.th32ProcessID,&otherSession) || otherSession==session) continue;
        Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,entry.th32ProcessID));
        if (!process.get()) continue;
        if (Within(fs::path(ImagePath(process.get())),root) && UserSid(process.get())==sid)
            throw std::runtime_error("This installation is running in another login session. Sign out of that session before changing files.");
    }
}

void RejectPinnedModules(const fs::path& root, const std::wstring& sid) {
    Handle processes(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    if (processes.get() == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot inspect Explorer module ownership.");
    PROCESSENTRY32W entry{sizeof(entry)};
    for (BOOL found = Process32FirstW(processes.get(), &entry); found; found = Process32NextW(processes.get(), &entry)) {
        if (_wcsicmp(entry.szExeFile, L"explorer.exe") != 0) continue;
        Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID));
        if (!process.get()) continue;
        try { if (UserSid(process.get()) != sid) continue; }
        catch (const std::exception&) { continue; }
        Handle modules(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, entry.th32ProcessID));
        if (modules.get() == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot prove the installed taskbar DLL is no longer mapped. Sign out and retry.");
        MODULEENTRY32W module{sizeof(module)};
        for (BOOL item = Module32FirstW(modules.get(), &module); item; item = Module32NextW(modules.get(), &module)) {
            if ((_wcsicmp(module.szModule, L"WorkFor.Taskbar.dll") == 0 || _wcsicmp(module.szModule, L"WorkFor.Taskbar.Live.dll") == 0)
                && Within(module.szExePath, root))
                throw std::runtime_error("WorkFor taskbar code remains pinned in Explorer. Sign out and sign back in before upgrading or uninstalling. Explorer will not be terminated.");
        }
    }
}

void PrepareRemoval(const fs::path& root, const std::wstring& sid, DWORD session) {
    ValidateInstallationRoot(root);
    RejectOtherSessionProcesses(root,sid,session);
    const auto server = Control(root, sid, session, "host.shutdown", false, 2500, true);
    CloseOwnedUi(root, sid);
    if (server) {
        Handle process(OpenProcess(SYNCHRONIZE, FALSE, *server));
        if (process.get() && WaitForSingleObject(process.get(), 15000) != WAIT_OBJECT_0)
            throw std::runtime_error("The installed Host is still detaching; retry shortly.");
    }
    RejectPinnedModules(root, sid);
}

void RemoveInventory(const fs::path& root) {
    Package package(root, false);
    std::set<fs::path> directories;
    if (!package.manifest.at("files").is_array() || package.manifest.at("files").size() > 20000)
        throw std::runtime_error("Invalid removal inventory.");
    // Validate every destination before removing the first file. Never recurse into an arbitrary tree.
    std::vector<fs::path> files;
    for (const auto& item : package.manifest.at("files")) {
        const fs::path relative(Wide(item.at("path").get<std::string>()));
        if (relative.empty() || relative.is_absolute() || relative.has_root_name()) throw std::runtime_error("Invalid removal path.");
        for (const auto& part : relative) if (part == L"..") throw std::runtime_error("Removal path escapes installation.");
        const auto path = root / relative;
        if (!Within(path,root)) throw std::runtime_error("Removal target escapes installation.");
        if (fs::exists(path)) {
            const auto verified = Asset(root,item.at("path").get<std::string>());
            if (!SamePath(verified,root / L"WorkFor.exe")) files.push_back(verified);
        }
        for (auto parent = path.parent_path(); parent != root && Within(parent,root); parent = parent.parent_path()) directories.insert(parent);
    }
    for (const auto& file : files) if (!fs::remove(file)) throw std::runtime_error("Cannot remove an installed file.");
    std::vector<fs::path> ordered(directories.begin(),directories.end());
    std::sort(ordered.begin(),ordered.end(),[](const fs::path& a,const fs::path& b){return a.native().size()>b.native().size();});
    for (const auto& directory : ordered) { std::error_code error; fs::remove(directory,error); } // Only empty directories; preserve unexpected user files.
}

json TestRemovalInventory() {
    wchar_t temporary[MAX_PATH]{};
    if (!GetTempPathW(MAX_PATH,temporary)) throw std::runtime_error("Cannot create removal-test directory.");
    GUID id{}; if (FAILED(CoCreateGuid(&id))) throw std::runtime_error("Cannot create removal-test identity.");
    wchar_t identity[64]{}; StringFromGUID2(id,identity,64);
    const fs::path root = fs::path(temporary) / (std::wstring(L"WorkFor-removal-test-") + identity);
    if (fs::exists(root)) throw std::runtime_error("Removal-test directory already exists.");
    fs::create_directories(root / L"app/1/host");
    const auto write = [](const fs::path& file,const std::string& text) { std::ofstream stream(file,std::ios::binary); stream << text; if (!stream) throw std::runtime_error("Cannot write removal fixture."); };
    write(root / L"WorkFor.exe","launcher fixture");
    write(root / L"app/1/host/known.dll","known installation asset");
    write(root / L"user-extra.txt","must stay");
    write(root / L".workfor-install",InstallMarker);
    write(root / L"release.json",json{{"schemaVersion",1},{"version","0.1.0"},
        {"files",json::array({json{{"path","WorkFor.exe"}},json{{"path","app/1/host/known.dll"}}})}}.dump());
    bool wrongRootRejected{};
    try { ValidateInstallationRoot(root); } catch (const std::exception&) { wrongRootRejected = true; }
    RemoveInventory(root);
    const bool knownRemoved = !fs::exists(root/L"app/1/host/known.dll");
    const bool emptyDirectoriesRemoved = !fs::exists(root/L"app");
    const bool launcherPreserved = fs::exists(root/L"WorkFor.exe");
    const bool unknownPreserved = fs::exists(root/L"user-extra.txt");
    write(root / L"must-survive.txt","must survive an invalid inventory");
    const fs::path outside = root.parent_path() / (root.filename().wstring()+L"-outside.txt");
    write(outside,"outside owned fixture");
    const auto outsideRelative = std::string("../") + outside.filename().string(); // Generated basename is ASCII + GUID only.
    write(root / L"release.json",json{{"schemaVersion",1},{"version","0.1.0"},
        {"files",json::array({json{{"path","must-survive.txt"}},json{{"path",outsideRelative}}})}}.dump());
    bool escapeRejected{};
    try { RemoveInventory(root); } catch (const std::exception&) { escapeRejected = true; }
    const bool noPartialDeletion = fs::exists(root / L"must-survive.txt") && fs::exists(outside);
    const bool passed = wrongRootRejected && knownRemoved && emptyDirectoriesRemoved && launcherPreserved
        && unknownPreserved && escapeRejected && noPartialDeletion;
    if (!passed) throw std::runtime_error("Removal inventory experiment failed; fixture retained for inspection.");
    for (const auto& path : {root/L"WorkFor.exe",root/L"user-extra.txt",root/L".workfor-install",root/L"must-survive.txt",root/L"release.json",outside}) fs::remove(path);
    fs::remove(root);
    return {{"passed",true},{"wrongRootRejected",wrongRootRejected},{"knownRemoved",knownRemoved},
        {"emptyDirectoriesRemoved",emptyDirectoriesRemoved},{"launcherPreserved",launcherPreserved},
        {"unknownPreserved",unknownPreserved},{"escapeRejected",escapeRejected},{"noPartialDeletion",noPartialDeletion}};
}

void WriteOutput(const std::string& text) {
    const auto output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (output && output != INVALID_HANDLE_VALUE) { DWORD written{}; WriteFile(output, text.data(), static_cast<DWORD>(text.size()), &written, nullptr); }
}
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    bool quiet{};
    try {
        int argc{}; LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
        if (!argv) throw std::runtime_error("Cannot parse command line.");
        const std::wstring mode = argc == 1 ? L"" : argv[1]; const int count = argc; LocalFree(argv);
        if (count > 2 || (mode != L"" && mode != L"--background" && mode != L"--settings" && mode != L"--quit" && mode != L"--verify"
            && mode != L"--prepare-uninstall" && mode != L"--uninstall-files" && mode != L"--self-test-removal"))
            throw std::runtime_error("Usage: WorkFor.exe [--background | --settings | --quit | --verify | --prepare-uninstall | --uninstall-files]");
        quiet = mode == L"--verify" || mode == L"--background" || mode == L"--quit" || mode == L"--prepare-uninstall" || mode == L"--uninstall-files" || mode == L"--self-test-removal";
        if (mode == L"--self-test-removal") { WriteOutput(TestRemovalInventory().dump()+"\n"); return 0; }
        const auto root = ExecutableDirectory();
        if (mode == L"--verify") { Package package(root); package.Verify(true); WriteOutput(json{{"valid",true},{"version",package.manifest.at("version")},{"signature","unsigned"}}.dump() + "\n"); return 0; }
        const auto sid = UserSid(); DWORD session{};
        if (!ProcessIdToSessionId(GetCurrentProcessId(), &session)) throw std::runtime_error("Cannot identify current session.");
        const auto mutexName = L"Local\\WorkFor.Launcher-" + sid + L"-" + std::to_wstring(session);
        Handle ownership(CreateMutexW(nullptr, FALSE, mutexName.c_str()));
        if (!ownership.get()) throw std::runtime_error("Cannot create launcher ownership handle.");
        const auto acquired = WaitForSingleObject(ownership.get(), 15000);
        if (acquired != WAIT_OBJECT_0 && acquired != WAIT_ABANDONED) throw std::runtime_error("Another WorkFor launch is in progress.");
        struct Release { HANDLE mutex; ~Release() { ReleaseMutex(mutex); } } release{ownership.get()};
        if (mode == L"--prepare-uninstall" || mode == L"--uninstall-files") {
            PrepareRemoval(root,sid,session);
            if (mode == L"--uninstall-files") RemoveInventory(root);
            WriteOutput(json{{"valid",true},{"prepared",true}}.dump()+"\n");
            return 0;
        }
        Package package(root); package.Verify(false);
        if (mode == L"--quit") {
            const auto server = Control(package.root, sid, session, "host.shutdown", false, 2500);
            CloseOwnedUi(package.root,sid);
            if (server) {
                Handle process(OpenProcess(SYNCHRONIZE, FALSE, *server));
                if (process.get() && WaitForSingleObject(process.get(), 15000) != WAIT_OBJECT_0) throw std::runtime_error("Host is still detaching; try again shortly.");
            }
            return 0;
        }
        if (!Control(package.root, sid, session, "diagnostics.get", false, 500)) {
            const auto launcher = package.root / L"WorkFor.exe";
            Start(package.host, {L"--script-host",package.script.wstring(),L"--ui",package.ui.wstring(),
                L"--native-bridge",package.native.wstring(),L"--launcher",launcher.wstring()});
            const auto deadline = GetTickCount64() + 10000;
            bool ready{};
            while (GetTickCount64() < deadline) {
                if (Control(package.root, sid, session, "diagnostics.get", false, 250)) { ready = true; break; }
                Sleep(50);
            }
            if (!ready) throw std::runtime_error("WorkFor Host failed to start. Check installation and runtime logs.");
        }
        if (mode != L"--background") {
            const auto pid = Start(package.ui, mode == L"--settings" ? std::vector<std::wstring>{L"--settings"} : std::vector<std::wstring>{});
            AllowSetForegroundWindow(pid);
        }
        return 0;
    }
    catch (const std::exception& error) {
        WriteOutput(json{{"valid",false},{"error",error.what()}}.dump() + "\n");
        if (!quiet) MessageBoxW(nullptr, Wide(error.what()).c_str(), L"WorkFor", MB_OK | MB_ICONERROR);
        return 1;
    }
}
