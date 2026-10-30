#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <tlhelp32.h>
#include <aclapi.h>
#include <sddl.h>
#include <stdexcept>
#include <filesystem>
#include <string>
#include <vector>
#include <cwctype>
#include "version.h"
#include "verified-worker.h"

#ifdef _M_IX86
#pragma comment(linker, "/EXPORT:CompareVersions=_CompareVersions@8")
#pragma comment(linker, "/EXPORT:ValidateDirectory=_ValidateDirectory@8")
#pragma comment(linker, "/EXPORT:HasDesktopRuntime=_HasDesktopRuntime@4")
#pragma comment(linker, "/EXPORT:StopApplications=_StopApplications@4")
#pragma comment(linker, "/EXPORT:ReplaceShim=_ReplaceShim@8")
#pragma comment(linker, "/EXPORT:RemoveShim=_RemoveShim@4")
#pragma comment(linker, "/EXPORT:RunningApplications=_RunningApplications@4")
#pragma comment(linker, "/EXPORT:UpdateApplicationsList=_UpdateApplicationsList@8")
#pragma comment(linker, "/EXPORT:BackgroundRunning=_BackgroundRunning@4")
#pragma comment(linker, "/EXPORT:ConfigureInstall=_ConfigureInstall@8")
#pragma comment(linker, "/EXPORT:CleanupInstall=_CleanupInstall@12")
#pragma comment(linker, "/EXPORT:RefreshShims=_RefreshShims@8")
#pragma comment(linker, "/EXPORT:JoinSetupWorker=_JoinSetupWorker@4")
#pragma comment(linker, "/EXPORT:RunElevatedSetup=_RunElevatedSetup@12")
#pragma comment(linker, "/EXPORT:BeginSetup=_BeginSetup@0")
#pragma comment(linker, "/EXPORT:CleanupUserInstall=_CleanupUserInstall@8")
#pragma comment(linker, "/EXPORT:PrepareOriginBroker=_PrepareOriginBroker@12")
#pragma comment(linker, "/EXPORT:RemoveOriginTasks=_RemoveOriginTasks@4")
#pragma comment(linker, "/EXPORT:MaintainOriginTask=_MaintainOriginTask@12")
#endif

extern "C" __declspec(dllexport) DWORD __stdcall CompareVersions(const wchar_t* left,const wchar_t* right) {
    try {auto value=pathman_version::compare(left?left:L"",right?right:L"");return value>0?1:value<0?2:0;}
    catch(...) {return 3;}
}

namespace {
constexpr auto settings = L"Software\\PathMan";
std::wstring folder(REFKNOWNFOLDERID id) {
    PWSTR p = nullptr;
    if (FAILED(SHGetKnownFolderPath(id, 0, nullptr, &p))) return {};
    std::wstring result(p); CoTaskMemFree(p); return result;
}
std::wstring read(HKEY root, const wchar_t* key, const wchar_t* name) {
    DWORD size = 0;
    if (RegGetValueW(root, key, name, RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_NOEXPAND, nullptr, nullptr, &size)) return {};
    std::vector<wchar_t> buffer(size / sizeof(wchar_t) + 1);
    if (RegGetValueW(root, key, name, RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_NOEXPAND, nullptr, buffer.data(), &size)) return {};
    return buffer.data();
}
std::wstring volume(std::filesystem::path path) {
    std::error_code error;
    while (!path.empty() && !std::filesystem::exists(path, error)) {
        const auto parent = path.parent_path();
        if (parent == path) return {};
        path = parent;
    }
    wchar_t root[32768]{}, name[32768]{};
    if (!GetVolumePathNameW(path.c_str(), root, 32768) || !GetVolumeNameForVolumeMountPointW(root, name, 32768)) return {};
    return name;
}
std::wstring normalized(std::wstring value) {
    const auto first = value.find_first_not_of(L" \t\"");
    if (first == std::wstring::npos) return {};
    value = value.substr(first, value.find_last_not_of(L" \t\"") - first + 1);
    while (value.size() > 3 && (value.back() == L'\\' || value.back() == L'/')) value.pop_back();
    for (auto& c : value) { if (c == L'/') c = L'\\'; c = static_cast<wchar_t>(towlower(c)); }
    return value;
}
bool owns(HKEY root, const wchar_t* flag) {
    DWORD value = 0, size = sizeof(value);
    return !RegGetValueW(root, settings, flag, RRF_RT_REG_DWORD, nullptr, &value, &size) && value == 1;
}
// Avoid NSIS string truncation of long PATH values. Remove only entries this
// installation added, retaining entries that were already present beforehand.
DWORD change_path(const std::wstring& directory, bool machine, const wchar_t* flag) {
    const HKEY root = machine ? HKEY_LOCAL_MACHINE : HKEY_CURRENT_USER;
    if (!owns(root, flag)) return ERROR_SUCCESS;
    const auto key = machine ? L"SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment" : L"Environment";
    HKEY environment = nullptr;
    auto error = RegOpenKeyExW(root, key, 0, KEY_QUERY_VALUE | KEY_SET_VALUE, &environment);
    if (error == ERROR_FILE_NOT_FOUND) return ERROR_SUCCESS;
    if (error) return error;
    const auto previous = read(root, key, L"Path");
    std::wstring updated;
    size_t start = 0;
    bool first = true;
    while (!previous.empty() && start <= previous.size()) {
        const auto end = previous.find(L';', start);
        const auto entry = previous.substr(start, end == std::wstring::npos ? end : end - start);
        if (normalized(entry) != normalized(directory)) {
            if (!first) updated += L';';
            first = false; updated += entry;
        }
        if (end == std::wstring::npos) break;
        start = end + 1;
    }
    const bool changed = updated != previous;
    if (changed) {
        DWORD type = REG_EXPAND_SZ;
        RegQueryValueExW(environment, L"Path", nullptr, &type, nullptr, nullptr);
        if (type != REG_SZ && type != REG_EXPAND_SZ) type = REG_EXPAND_SZ;
        error = RegSetValueExW(environment, L"Path", 0, type, reinterpret_cast<const BYTE*>(updated.c_str()), static_cast<DWORD>((updated.size() + 1) * sizeof(wchar_t)));
    }
    RegCloseKey(environment);
    DWORD_PTR ignored = 0;
    if (changed && !error)
        SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, 0, reinterpret_cast<LPARAM>(L"Environment"), SMTO_ABORTIFHUNG, 2000, &ignored);
    return error;
}
DWORD allow_links(const std::filesystem::path& shim) {
    PACL previous = nullptr, updated = nullptr;
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    auto error = GetNamedSecurityInfoW(shim.c_str(), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION, nullptr, nullptr, &previous, nullptr, &descriptor);
    if (error) return error;
    BYTE sid[SECURITY_MAX_SID_SIZE]{};
    DWORD size = sizeof(sid);
    if (!CreateWellKnownSid(WinBuiltinUsersSid, nullptr, sid, &size)) { LocalFree(descriptor); return GetLastError(); }
    EXPLICIT_ACCESSW access{};
    // Hard links need WriteAttributes on the source, never write-data/modify.
    access.grfAccessPermissions = FILE_WRITE_ATTRIBUTES;
    access.grfAccessMode = GRANT_ACCESS;
    access.Trustee.TrusteeForm = TRUSTEE_IS_SID;
    access.Trustee.TrusteeType = TRUSTEE_IS_WELL_KNOWN_GROUP;
    access.Trustee.ptstrName = reinterpret_cast<LPWSTR>(sid);
    error = SetEntriesInAclW(1, &access, previous, &updated);
    if (!error) error = SetNamedSecurityInfoW(const_cast<LPWSTR>(shim.c_str()), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION, nullptr, nullptr, updated, nullptr);
    if (updated) LocalFree(updated);
    LocalFree(descriptor); return error;
}
void remove_shortcut(const std::filesystem::path& shortcut, const std::filesystem::path& expected) {
    const auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    IShellLinkW* link = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link)))) {
        IPersistFile* persist = nullptr;
        if (SUCCEEDED(link->QueryInterface(IID_PPV_ARGS(&persist)))) {
            wchar_t target[32768]{};
            if (SUCCEEDED(persist->Load(shortcut.c_str(), STGM_READ)) && SUCCEEDED(link->GetPath(target, 32768, nullptr, SLGP_RAWPATH)) && _wcsicmp(target, expected.c_str()) == 0) DeleteFileW(shortcut.c_str());
            persist->Release();
        }
        link->Release();
    }
    if (SUCCEEDED(initialized)) CoUninitialize();
}
bool file_exists(const std::filesystem::path& path) {
    const auto attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
}
bool runtime_component(const std::filesystem::path& root, const wchar_t* category, const wchar_t* file) {
    WIN32_FIND_DATAW found{};
    const auto search = root / category / L"10.0.*";
    const auto handle = FindFirstFileW(search.c_str(), &found);
    if (handle == INVALID_HANDLE_VALUE) return false;
    bool present = false;
    do {
        if ((found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) &&
            std::wstring(found.cFileName).find(L'-') == std::wstring::npos &&
            file_exists(root / category / found.cFileName / file)) { present = true; break; }
    } while (FindNextFileW(handle, &found));
    FindClose(handle);
    return present;
}
struct running_app { DWORD id; bool gui; bool background; };
DWORD applications(const std::filesystem::path& root, std::vector<running_app>& result) {
    const auto snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return GetLastError();
    PROCESSENTRY32W entry{sizeof(entry)};
    if (Process32FirstW(snapshot, &entry)) do {
        if (entry.th32ProcessID == GetCurrentProcessId()) continue;
        const auto process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID);
        if (!process) continue;
        wchar_t path[32768]{}; DWORD length = 32768;
        if (QueryFullProcessImageNameW(process, 0, path, &length)) {
            const bool gui = _wcsicmp(path, (root / L"PathMan.exe").c_str()) == 0;
            const bool background = _wcsicmp(path, (root / L"internal" / L"PathMan.Service.exe").c_str()) == 0;
            const bool cli = _wcsicmp(path, (root / L"bin" / L"pathman.exe").c_str()) == 0;
            if (gui || background || cli) result.push_back({entry.th32ProcessID, gui, background});
        }
        CloseHandle(process);
    } while (Process32NextW(snapshot, &entry));
    CloseHandle(snapshot);
    return ERROR_SUCCESS;
}
}
extern "C" __declspec(dllexport) DWORD __stdcall ValidateDirectory(const wchar_t* directory, const wchar_t* localData) {
    try {
        const std::wstring path(directory);
        if (!std::filesystem::path(path).is_absolute() || path.find_first_of(L"\"<>|?*") != std::wstring::npos) return ERROR_BAD_PATHNAME;
        const auto target = volume(path);
        if (target.empty()) return ERROR_BAD_PATHNAME;
        const auto userVolume = volume(localData);
        DWORD flags = 0;
        if (userVolume.empty() || !GetVolumeInformationW(userVolume.c_str(), nullptr, 0, nullptr, nullptr, &flags, nullptr, 0) || !(flags & FILE_SUPPORTS_HARD_LINKS)) return ERROR_NOT_SUPPORTED;
        return ERROR_SUCCESS;
    } catch (...) { return ERROR_BAD_PATHNAME; }
}
extern "C" __declspec(dllexport) BOOL __stdcall HasDesktopRuntime(const wchar_t* arch) {
    const auto base = std::wstring(L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\") + arch;
    const auto key = base + L"\\sharedfx\\Microsoft.WindowsDesktop.App";
    for (const auto view : {KEY_WOW64_64KEY, KEY_WOW64_32KEY}) {
        HKEY installed = nullptr;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, base.c_str(), 0, KEY_QUERY_VALUE | view, &installed)) continue;
        const std::filesystem::path root(read(installed, nullptr, L"InstallLocation"));
        RegCloseKey(installed);
        // Runtime uninstall can leave version values behind. Require the host,
        // managed runtime and Desktop files, not just a registry version marker.
        if (root.empty() || !runtime_component(root, L"host\\fxr", L"hostfxr.dll") ||
            !runtime_component(root, L"shared\\Microsoft.NETCore.App", L"coreclr.dll")) continue;
        HKEY opened = nullptr;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, key.c_str(), 0, KEY_QUERY_VALUE | view, &opened)) continue;
        for (DWORD index = 0;; ++index) {
            wchar_t name[256]{}; DWORD length = 256;
            if (RegEnumValueW(opened, index, name, &length, nullptr, nullptr, nullptr, nullptr)) break;
            if (std::wstring(name).starts_with(L"10.0.") && std::wstring(name).find(L'-') == std::wstring::npos &&
                file_exists(root / L"shared" / L"Microsoft.WindowsDesktop.App" / name / L"PresentationFramework.dll")) { RegCloseKey(opened); return TRUE; }
        }
        RegCloseKey(opened);
    }
    return FALSE;
}
extern "C" __declspec(dllexport) DWORD __stdcall StopApplications(const wchar_t* directory) {
    try {
        const std::filesystem::path root(directory);
        std::vector<running_app> running;
        auto error = applications(root, running);
        if (error || running.empty()) return error;
        for (const auto& app : running) {
            if (!app.gui && !app.background) continue; // CLI commands finish on their own.
            const HANDLE process = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, app.id);
            if (!process) { if (GetLastError() == ERROR_INVALID_PARAMETER) continue; return GetLastError(); }
            wchar_t path[32768]{}; DWORD length = 32768;
            const bool owned = QueryFullProcessImageNameW(process, 0, path, &length) &&
                (_wcsicmp(path, (root / L"PathMan.exe").c_str()) == 0 ||
                 _wcsicmp(path, (root / L"internal" / L"PathMan.Service.exe").c_str()) == 0);
            if (owned && !TerminateProcess(process, ERROR_PROCESS_ABORTED)) {
                const auto failure = GetLastError();
                if (WaitForSingleObject(process, 0) != WAIT_OBJECT_0) error = failure;
            }
            if (owned && !error && WaitForSingleObject(process, 5000) != WAIT_OBJECT_0) error = ERROR_BUSY;
            CloseHandle(process);
            if (error) return error;
        }
        running.clear(); error = applications(root, running);
        if (error || running.empty()) return error;
        return ERROR_BUSY;
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}
namespace {
void cleanup_retired(const std::filesystem::path& directory) {
    std::error_code error;
    for (std::filesystem::directory_iterator files(directory, error), end; files != end; files.increment(error)) {
        if (error) break;
        const auto name = files->path().filename().wstring();
        if (name.starts_with(L".pathman-retired-") && name.ends_with(L".tmp")) DeleteFileW(files->path().c_str());
    }
}
std::filesystem::path retired_name(const std::filesystem::path& directory) {
    GUID id{};
    if (FAILED(CoCreateGuid(&id))) throw std::runtime_error("Cannot create temporary name");
    wchar_t text[40]{}; StringFromGUID2(id, text, 40);
    return directory / (std::wstring(L".pathman-retired-") + text + L".tmp");
}
}
extern "C" __declspec(dllexport) DWORD __stdcall ReplaceShim(const wchar_t* source, const wchar_t* destination) {
    try {
        const auto directory = std::filesystem::path(destination).parent_path();
        cleanup_retired(directory);
        const auto retired = retired_name(directory);
        bool moved = false;
        if (GetFileAttributesW(destination) != INVALID_FILE_ATTRIBUTES) {
            if (!MoveFileExW(destination, retired.c_str(), 0)) return GetLastError();
            moved = true;
        }
        if (!MoveFileExW(source, destination, 0)) {
            const auto error = GetLastError();
            if (moved) MoveFileExW(retired.c_str(), destination, 0);
            return error;
        }
        if (moved) DeleteFileW(retired.c_str());
        return ERROR_SUCCESS;
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}
extern "C" __declspec(dllexport) DWORD __stdcall RemoveShim(const wchar_t* directory) {
    try {
        const auto parent = std::filesystem::path(directory) / L"internal";
        const auto path = parent / L"PathMan.Shim.exe";
        cleanup_retired(parent);
        const auto retired = retired_name(parent);
        if (!MoveFileExW(path.c_str(), retired.c_str(), 0)) {
            const auto error = GetLastError();
            return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND ? ERROR_SUCCESS : error;
        }
        DeleteFileW(retired.c_str()); return ERROR_SUCCESS;
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}
extern "C" __declspec(dllexport) int __stdcall RunningApplications(const wchar_t* directory) {
    try {
        std::vector<running_app> running;
        return applications(std::filesystem::path(directory), running) == ERROR_SUCCESS ? static_cast<int>(running.size()) : -1;
    } catch (...) { return -1; }
}
extern "C" __declspec(dllexport) int __stdcall UpdateApplicationsList(const wchar_t* directory, HWND list) {
    try {
        std::vector<running_app> running;
        if (applications(std::filesystem::path(directory), running) != ERROR_SUCCESS || !IsWindow(list)) return -1;
        bool gui = false, background = false, cli = false;
        for (const auto& app : running) {
            gui |= app.gui;
            background |= app.background;
            cli |= !app.gui && !app.background;
        }
        // Show each executable once, regardless of how many instances are open.
        std::vector<std::wstring> names;
        if (gui) names.emplace_back(L"PathMan.exe");
        if (background) names.emplace_back(L"PathMan.Service.exe");
        if (cli) names.emplace_back(L"pathman.exe");
        bool changed = SendMessageW(list, LB_GETCOUNT, 0, 0) != static_cast<LRESULT>(names.size());
        for (size_t index = 0; !changed && index < names.size(); ++index) {
            const auto length = SendMessageW(list, LB_GETTEXTLEN, index, 0);
            if (length < 0 || length > 32768) { changed = true; break; }
            std::vector<wchar_t> text(static_cast<size_t>(length) + 1);
            SendMessageW(list, LB_GETTEXT, index, reinterpret_cast<LPARAM>(text.data()));
            changed = names[index] != text.data();
        }
        if (changed) {
            SendMessageW(list, WM_SETREDRAW, FALSE, 0);
            SendMessageW(list, LB_RESETCONTENT, 0, 0);
            for (const auto& name : names) SendMessageW(list, LB_ADDSTRING, 0, reinterpret_cast<LPARAM>(name.c_str()));
            SendMessageW(list, WM_SETREDRAW, TRUE, 0);
            InvalidateRect(list, nullptr, TRUE);
        }
        return static_cast<int>(running.size());
    } catch (...) { return -1; }
}
extern "C" __declspec(dllexport) BOOL __stdcall BackgroundRunning(const wchar_t* directory) {
    try {
        std::vector<running_app> running;
        if (applications(std::filesystem::path(directory), running) != ERROR_SUCCESS) return FALSE;
        DWORD session = 0; ProcessIdToSessionId(GetCurrentProcessId(), &session);
        for (const auto& app : running) {
            DWORD other = 0;
            if (app.background && ProcessIdToSessionId(app.id, &other) && session == other) return TRUE;
        }
    } catch (...) {}
    return FALSE;
}
extern "C" __declspec(dllexport) DWORD __stdcall ConfigureInstall(const wchar_t* directory, BOOL machine) {
    try {
        const std::filesystem::path root(directory);
        auto error = machine ? allow_links(root / L"internal" / L"PathMan.Shim.exe") : ERROR_SUCCESS;
        return error;
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}
extern "C" __declspec(dllexport) DWORD __stdcall CleanupInstall(const wchar_t* directory, BOOL machine, const wchar_t* desktop) {
    try {
        const std::filesystem::path root(directory);
        auto error = change_path((root / L"bin").wstring(), machine != FALSE, L"BinPathOwned");
        if (!machine) {
            const auto next = change_path(folder(FOLDERID_LocalAppData) + L"\\PathMan\\shims", false, L"ShimsPathOwned");
            if (!error) error = next;
        }
        remove_shortcut(std::filesystem::path(desktop) / L"PathMan.lnk", root / L"PathMan.exe");
        return error;
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}
extern "C" __declspec(dllexport) DWORD __stdcall CleanupUserInstall(const wchar_t* directory, const wchar_t* desktop) {
    try {
        const std::filesystem::path root(directory);
        for (const auto name : { L"AppUserModelId\\PathMan.PathMan", L"AppUserModelId\\PathMan.UpdateTest", L"pathman-update", L"pathman-update-test" }) {
            const auto key = std::wstring(L"Software\\Classes\\") + name;
            if (normalized(read(HKEY_CURRENT_USER, key.c_str(), L"ExecutablePath")) == normalized((root / L"PathMan.exe").wstring()))
                RegDeleteTreeW(HKEY_CURRENT_USER, key.c_str());
        }
        constexpr auto run = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const auto expected = L"\"" + (root / L"internal" / L"PathMan.Service.exe").wstring() + L"\"";
        if (_wcsicmp(read(HKEY_CURRENT_USER, run, L"PathMan").c_str(), expected.c_str()) == 0) {
            HKEY opened = nullptr;
            if (!RegOpenKeyExW(HKEY_CURRENT_USER, run, 0, KEY_SET_VALUE, &opened)) { RegDeleteValueW(opened, L"PathMan"); RegCloseKey(opened); }
        }
        remove_shortcut(std::filesystem::path(desktop) / L"PathMan.lnk", root / L"PathMan.exe");
        return change_path(folder(FOLDERID_LocalAppData) + L"\\PathMan\\shims", false, L"ShimsPathOwned");
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}
extern "C" __declspec(dllexport) DWORD __stdcall RefreshShims(const wchar_t* directory, const wchar_t* localData) {
    try {
        const auto data = std::filesystem::path(localData) / L"PathMan";
        const auto helper = std::filesystem::path(directory) / L"internal" / L"PathMan.Service.exe";
        auto command = L"\"" + helper.wstring() + L"\" --installer-refresh-shims \"" + data.wstring() + L"\"";
        STARTUPINFOW start{sizeof(start)}; PROCESS_INFORMATION process{};
        if (!CreateProcessW(helper.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, directory, &start, &process)) return GetLastError();
        WaitForSingleObject(process.hProcess, INFINITE);
        DWORD exitCode = ERROR_INSTALL_FAILURE;
        GetExitCodeProcess(process.hProcess, &exitCode);
        CloseHandle(process.hThread); CloseHandle(process.hProcess);
        return exitCode;
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}

// A one-use event authorizes the silent elevated child. Both processes retain
// the same global lock, including if the visible wizard exits unexpectedly.
extern "C" __declspec(dllexport) HANDLE __stdcall BeginSetup() {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)(A;;0x00100000;;;AU)", SDDL_REVISION_1, &descriptor, nullptr)) return nullptr;
    SECURITY_ATTRIBUTES attributes{sizeof(attributes), descriptor, FALSE};
    SetLastError(ERROR_SUCCESS);
    const auto mutex = CreateMutexW(&attributes, FALSE, L"Global\\PathMan.Setup");
    const auto error = GetLastError();
    LocalFree(descriptor);
    if (mutex && error == ERROR_ALREADY_EXISTS) { CloseHandle(mutex); return nullptr; }
    return mutex;
}
extern "C" __declspec(dllexport) HANDLE __stdcall JoinSetupWorker(const wchar_t* ticket) {
    GUID id{};
    if (FAILED(CLSIDFromString(ticket, &id))) return nullptr;
    const auto name = std::wstring(L"Local\\PathMan.Setup.Worker.") + ticket;
    const auto event = OpenEventW(SYNCHRONIZE, FALSE, name.c_str());
    if (!event) return nullptr;
    const auto allowed = WaitForSingleObject(event, 0) == WAIT_OBJECT_0;
    CloseHandle(event);
    return allowed ? OpenMutexW(SYNCHRONIZE, FALSE, L"Global\\PathMan.Setup") : nullptr;
}
namespace {
std::wstring current_sid() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return {};
    DWORD size = 0; GetTokenInformation(token, TokenUser, nullptr, 0, &size);
    std::vector<BYTE> buffer(size);
    const bool read = GetTokenInformation(token, TokenUser, buffer.data(), size, &size) != FALSE;
    CloseHandle(token);
    PWSTR sid = nullptr;
    if (!read || !ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &sid)) return {};
    std::wstring result(sid); LocalFree(sid); return result;
}
void wait_with_messages(HANDLE process) {
    bool quit = false; WPARAM exit = 0;
    while (MsgWaitForMultipleObjects(1, &process, FALSE, INFINITE, QS_ALLINPUT) == WAIT_OBJECT_0 + 1) {
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            if (message.message == WM_QUIT) { quit = true; exit = message.wParam; continue; }
            TranslateMessage(&message); DispatchMessageW(&message);
        }
    }
    if (quit) PostQuitMessage(static_cast<int>(exit));
}
DWORD run_verified_elevated(HWND owner, const std::filesystem::path& worker, const std::wstring& parameters) {
    std::vector<wchar_t> current(32768);
    const auto length = GetModuleFileNameW(nullptr, current.data(), static_cast<DWORD>(current.size()));
    if (!length || length >= current.size()) return ERROR_BAD_PATHNAME;
    pathman_setup::verified_worker verified(worker.c_str(), current.data());
    if (verified.error() != ERROR_SUCCESS) return verified.error();
    SHELLEXECUTEINFOW execute{sizeof(execute)};
    execute.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
    execute.hwnd = owner;
    execute.lpVerb = L"runas"; execute.lpFile = worker.c_str(); execute.lpParameters = parameters.c_str(); execute.nShow = SW_HIDE;
    const auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    const bool started = ShellExecuteExW(&execute) != FALSE;
    const auto launch_error = started ? ERROR_SUCCESS : GetLastError();
    if (SUCCEEDED(initialized)) CoUninitialize();
    if (!started) return launch_error == ERROR_CANCELLED ? ERROR_INSTALL_USEREXIT : launch_error;
    DWORD result = ERROR_INSTALL_FAILURE;
    if (execute.hProcess) {
        wait_with_messages(execute.hProcess);
        GetExitCodeProcess(execute.hProcess, &result);
        CloseHandle(execute.hProcess);
    }
    return result;
}
}
namespace {
DWORD origin_maintenance(const wchar_t* directory, const std::wstring& parameters) {
    try {
        const auto executable = std::filesystem::path(directory) / L"internal" / L"PathMan.Service.exe";
        if (!std::filesystem::exists(executable)) return ERROR_SUCCESS;
        auto command = L"\"" + executable.wstring() + L"\" " + parameters;
        STARTUPINFOW start{sizeof(start)}; PROCESS_INFORMATION process{};
        if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, directory, &start, &process)) return GetLastError();
        wait_with_messages(process.hProcess);
        DWORD result = ERROR_INSTALL_FAILURE;
        GetExitCodeProcess(process.hProcess, &result);
        CloseHandle(process.hThread); CloseHandle(process.hProcess);
        if (result != ERROR_ELEVATION_REQUIRED) return result;
        SHELLEXECUTEINFOW execute{sizeof(execute)};
        execute.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
        execute.lpVerb = L"runas"; execute.lpFile = executable.c_str(); execute.lpParameters = parameters.c_str(); execute.nShow = SW_HIDE;
        if (!ShellExecuteExW(&execute)) return GetLastError();
        if (!execute.hProcess) return ERROR_INSTALL_FAILURE;
        wait_with_messages(execute.hProcess);
        GetExitCodeProcess(execute.hProcess, &result); CloseHandle(execute.hProcess);
        return result;
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}
}
extern "C" __declspec(dllexport) DWORD __stdcall RemoveOriginTasks(const wchar_t* directory) {
    return origin_maintenance(directory, L"--remove-origin-tasks");
}
extern "C" __declspec(dllexport) DWORD __stdcall MaintainOriginTask(const wchar_t* directory, const wchar_t* localData, const wchar_t* originalSid) {
    const auto sid = originalSid && *originalSid ? std::wstring(originalSid) : current_sid();
    const auto data = std::filesystem::path(localData) / L"PathMan";
    return origin_maintenance(directory, L"--maintain-origin-task \"" + sid + L"\" \"" + data.wstring() + L"\"");
}
extern "C" __declspec(dllexport) DWORD __stdcall PrepareOriginBroker(const wchar_t* directory, const wchar_t* localData, const wchar_t* originalSid) {
    try {
        const auto sid = originalSid && *originalSid ? std::wstring(originalSid) : current_sid();
        if (sid.empty()) return ERROR_INVALID_SID;
        GUID id{}; wchar_t ticket[40]{};
        if (FAILED(CoCreateGuid(&id)) || !StringFromGUID2(id, ticket, 40)) return ERROR_INSTALL_FAILURE;
        const auto name = std::wstring(L"Local\\PathMan.Setup.Origins.") + ticket;
        const auto ready = CreateEventW(nullptr, FALSE, FALSE, name.c_str());
        if (!ready) return GetLastError();
        const auto executable = std::filesystem::path(directory) / L"internal" / L"PathMan.Service.exe";
        const auto data = std::filesystem::path(localData) / L"PathMan";
        auto command = L"\"" + executable.wstring() + L"\" --installer-origin-broker \"" + sid + L"\" \"" + data.wstring() + L"\" \"" + name + L"\"";
        STARTUPINFOW start{sizeof(start)}; PROCESS_INFORMATION process{};
        DWORD result = ERROR_SUCCESS;
        if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, directory, &start, &process)) result = GetLastError();
        else {
            HANDLE events[]{ready, process.hProcess};
            if (WaitForMultipleObjects(2, events, FALSE, 15000) == WAIT_TIMEOUT) result = ERROR_TIMEOUT;
            CloseHandle(process.hThread); CloseHandle(process.hProcess);
        }
        CloseHandle(ready); return result;
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}
extern "C" __declspec(dllexport) DWORD __stdcall RunElevatedSetup(HWND owner, const wchar_t* executable, const wchar_t* configuration) {
    const auto userSid = current_sid();
    if (userSid.empty() || !WritePrivateProfileStringW(L"Setup", L"OriginalUserSid", userSid.c_str(), configuration)) return ERROR_INSTALL_FAILURE;
    GUID id{}; wchar_t ticket[40]{};
    if (FAILED(CoCreateGuid(&id)) || !StringFromGUID2(id, ticket, 40)) return ERROR_INSTALL_FAILURE;
    const auto name = std::wstring(L"Local\\PathMan.Setup.Worker.") + ticket;
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)", SDDL_REVISION_1, &descriptor, nullptr)) return GetLastError();
    SECURITY_ATTRIBUTES attributes{sizeof(attributes), descriptor, FALSE};
    const auto event = CreateEventW(&attributes, FALSE, TRUE, name.c_str());
    const auto createError = GetLastError();
    LocalFree(descriptor);
    if (!event) return createError;
    // /S alone still permits NSIS's pre-script CRC dialog. The visible parent
    // has already checked CRC; verify identical bytes and retain a read lock
    // before skipping that redundant check in the authorized child.
    auto parameters = std::wstring(L"/S /NCRC /AllUsers /SetupWorker=") + ticket + L" /SetupConfig=\"" + configuration + L"\"";
    auto worker = std::filesystem::path(executable);
    const bool uninstall = _wcsicmp(worker.filename().c_str(), L"Uninstall.exe") == 0;
    if (uninstall) {
        // Avoid NSIS's asynchronous uninstaller bootstrap, and do not execute
        // the installed file that the worker needs to remove.
        const auto original = worker;
        worker = std::filesystem::path(configuration).parent_path() / L"uninstall-worker.exe";
        if (!CopyFileW(original.c_str(), worker.c_str(), FALSE)) { const auto error = GetLastError(); CloseHandle(event); return error; }
        parameters += L" _?=" + original.parent_path().wstring();
    }
    const auto result = run_verified_elevated(owner, worker, parameters);
    CloseHandle(event);
    if (uninstall) DeleteFileW(worker.c_str());
    return result;
}
