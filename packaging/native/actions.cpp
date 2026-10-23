#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <tlhelp32.h>
#include <aclapi.h>
#include <sddl.h>
#include <filesystem>
#include <string>
#include <vector>
#include <cwctype>

#ifdef _M_IX86
#pragma comment(linker, "/EXPORT:ValidateDirectory=_ValidateDirectory@8")
#pragma comment(linker, "/EXPORT:HasDesktopRuntime=_HasDesktopRuntime@4")
#pragma comment(linker, "/EXPORT:StopApplications=_StopApplications@4")
#pragma comment(linker, "/EXPORT:RunningApplications=_RunningApplications@4")
#pragma comment(linker, "/EXPORT:BackgroundRunning=_BackgroundRunning@4")
#pragma comment(linker, "/EXPORT:ConfigureInstall=_ConfigureInstall@8")
#pragma comment(linker, "/EXPORT:CleanupInstall=_CleanupInstall@12")
#pragma comment(linker, "/EXPORT:RefreshShims=_RefreshShims@8")
#pragma comment(linker, "/EXPORT:JoinSetupWorker=_JoinSetupWorker@4")
#pragma comment(linker, "/EXPORT:RunElevatedSetup=_RunElevatedSetup@12")
#pragma comment(linker, "/EXPORT:BeginSetup=_BeginSetup@0")
#pragma comment(linker, "/EXPORT:CleanupUserInstall=_CleanupUserInstall@8")
#endif

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
DWORD change_path(const std::wstring& directory, bool machine, bool add, const wchar_t* flag) {
    const HKEY root = machine ? HKEY_LOCAL_MACHINE : HKEY_CURRENT_USER;
    const auto key = machine ? L"SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment" : L"Environment";
    HKEY environment = nullptr;
    auto error = RegCreateKeyExW(root, key, 0, nullptr, 0, KEY_QUERY_VALUE | KEY_SET_VALUE, nullptr, &environment, nullptr);
    if (error) return error;
    const auto previous = read(root, key, L"Path");
    std::wstring updated;
    bool found = false;
    size_t start = 0;
    while (start < previous.size()) {
        const auto end = previous.find(L';', start);
        const auto entry = previous.substr(start, end == std::wstring::npos ? end : end - start);
        const bool match = normalized(entry) == normalized(directory);
        found |= match;
        if (add || !match || !owns(root, flag)) {
            if (!updated.empty()) updated += L';';
            updated += entry;
        }
        if (end == std::wstring::npos) break;
        start = end + 1;
    }
    if (add && !found) {
        updated = previous;
        if (!updated.empty() && updated.back() != L';') updated += L';';
        updated += directory;
    }
    if ((add && !found) || (!add && found && owns(root, flag))) {
        DWORD type = REG_EXPAND_SZ;
        RegQueryValueExW(environment, L"Path", nullptr, &type, nullptr, nullptr);
        if (type != REG_SZ && type != REG_EXPAND_SZ) type = REG_EXPAND_SZ;
        error = RegSetValueExW(environment, L"Path", 0, type, reinterpret_cast<const BYTE*>(updated.c_str()), static_cast<DWORD>((updated.size() + 1) * sizeof(wchar_t)));
        if (!error && add) {
            HKEY tracking = nullptr;
            error = RegCreateKeyExW(root, settings, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &tracking, nullptr);
            if (!error) {
                const DWORD owned = 1;
                error = RegSetValueExW(tracking, flag, 0, REG_DWORD, reinterpret_cast<const BYTE*>(&owned), sizeof(owned));
                RegCloseKey(tracking);
            }
        }
    }
    RegCloseKey(environment);
    DWORD_PTR ignored = 0;
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
bool same_file(const std::filesystem::path& left, const std::filesystem::path& right) {
    const auto a = CreateFileW(left.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    const auto b = CreateFileW(right.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    BY_HANDLE_FILE_INFORMATION x{}, y{};
    const bool equal = a != INVALID_HANDLE_VALUE && b != INVALID_HANDLE_VALUE && GetFileInformationByHandle(a, &x) && GetFileInformationByHandle(b, &y) &&
        x.dwVolumeSerialNumber == y.dwVolumeSerialNumber && x.nFileIndexHigh == y.nFileIndexHigh && x.nFileIndexLow == y.nFileIndexLow;
    if (a != INVALID_HANDLE_VALUE) CloseHandle(a);
    if (b != INVALID_HANDLE_VALUE) CloseHandle(b);
    return equal;
}
bool copied_alias(const std::filesystem::path& executable, const std::filesystem::path& root) {
    if (_wcsicmp(executable.parent_path().filename().c_str(), L"shims") != 0) return false;
    auto manifest = executable; manifest.replace_extension(L".shim");
    if (GetPrivateProfileIntW(L"PathMan.Shim", L"Format", 0, manifest.c_str()) != 1) return false;
    for (const auto hive : {HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE})
    for (const auto view : {KEY_WOW64_32KEY, KEY_WOW64_64KEY}) {
        HKEY uninstall = nullptr;
        if (RegOpenKeyExW(hive, L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\PathMan", 0, KEY_QUERY_VALUE | view, &uninstall)) continue;
        const auto location = read(uninstall, nullptr, L"InstallLocation");
        RegCloseKey(uninstall);
        if (normalized(location) == normalized(root.wstring())) return true;
    }
    return false;
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
            // An alias can run under any filename. Compare file identities so hard links are included.
            if (gui || background || cli || same_file(path, root / L"internal" / L"PathMan.Shim.exe") || copied_alias(path, root)) result.push_back({entry.th32ProcessID, gui, background});
        }
        CloseHandle(process);
    } while (Process32NextW(snapshot, &entry));
    CloseHandle(snapshot);
    return ERROR_SUCCESS;
}
BOOL CALLBACK close_window(HWND window, LPARAM parameter) {
    DWORD id = 0; GetWindowThreadProcessId(window, &id);
    // WPF can give its visible main window a hidden owner HWND. Do not filter by GW_OWNER.
    wchar_t title[256]{}; GetWindowTextW(window, title, 256);
    if (id == static_cast<DWORD>(parameter) && IsWindowVisible(window) && _wcsicmp(title, L"PathMan") == 0) PostMessageW(window, WM_CLOSE, 0, 0);
    return TRUE;
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
        for (const auto& app : running) if (app.gui) EnumWindows(close_window, static_cast<LPARAM>(app.id));
        // The CLI signals the normal background stop event, retaining its per-user/per-install identity.
        auto command = L"\"" + (root / L"bin" / L"pathman.exe").wstring() + L"\" background stop";
        STARTUPINFOW start{sizeof(start)}; PROCESS_INFORMATION process{};
        if (CreateProcessW((root / L"bin" / L"pathman.exe").c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, root.c_str(), &start, &process)) {
            WaitForSingleObject(process.hProcess, 6000); CloseHandle(process.hThread); CloseHandle(process.hProcess);
        }
        // Never discard unsaved edits or kill alias child commands. A refused WM_CLOSE stays retryable.
        for (int retry = 0; retry < 50; ++retry) {
            running.clear(); error = applications(root, running);
            if (error || running.empty()) return error;
            Sleep(100);
        }
        return ERROR_BUSY;
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}
extern "C" __declspec(dllexport) int __stdcall RunningApplications(const wchar_t* directory) {
    try {
        std::vector<running_app> running;
        return applications(std::filesystem::path(directory), running) == ERROR_SUCCESS ? static_cast<int>(running.size()) : -1;
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
        if (!error) error = change_path((root / L"bin").wstring(), machine != FALSE, true, L"BinPathOwned");
        return error;
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}
extern "C" __declspec(dllexport) DWORD __stdcall CleanupInstall(const wchar_t* directory, BOOL machine, const wchar_t* desktop) {
    try {
        const std::filesystem::path root(directory);
        auto error = change_path((root / L"bin").wstring(), machine != FALSE, false, L"BinPathOwned");
        if (!machine) {
            const auto next = change_path(folder(FOLDERID_LocalAppData) + L"\\PathMan\\shims", false, false, L"ShimsPathOwned");
            if (!error) error = next;
        }
        remove_shortcut(std::filesystem::path(desktop) / L"PathMan.lnk", root / L"PathMan.exe");
        return error;
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}
extern "C" __declspec(dllexport) DWORD __stdcall CleanupUserInstall(const wchar_t* directory, const wchar_t* desktop) {
    try {
        const std::filesystem::path root(directory);
        constexpr auto run = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const auto expected = L"\"" + (root / L"internal" / L"PathMan.Service.exe").wstring() + L"\"";
        if (_wcsicmp(read(HKEY_CURRENT_USER, run, L"PathMan").c_str(), expected.c_str()) == 0) {
            HKEY opened = nullptr;
            if (!RegOpenKeyExW(HKEY_CURRENT_USER, run, 0, KEY_SET_VALUE, &opened)) { RegDeleteValueW(opened, L"PathMan"); RegCloseKey(opened); }
        }
        remove_shortcut(std::filesystem::path(desktop) / L"PathMan.lnk", root / L"PathMan.exe");
        return change_path(folder(FOLDERID_LocalAppData) + L"\\PathMan\\shims", false, false, L"ShimsPathOwned");
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}
extern "C" __declspec(dllexport) DWORD __stdcall RefreshShims(const wchar_t* directory, const wchar_t* localData) {
    try {
        const auto data = std::filesystem::path(localData) / L"PathMan";
        if (!std::filesystem::exists(data / L"shims")) return ERROR_SUCCESS;
        const auto cli = std::filesystem::path(directory) / L"bin" / L"pathman.exe";
        auto command = L"\"" + cli.wstring() + L"\" --data-root \"" + data.wstring() + L"\" shim refresh";
        STARTUPINFOW start{sizeof(start)}; PROCESS_INFORMATION process{};
        if (!CreateProcessW(cli.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, directory, &start, &process)) return GetLastError();
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
extern "C" __declspec(dllexport) DWORD __stdcall RunElevatedSetup(HWND owner, const wchar_t* executable, const wchar_t* configuration) {
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
    auto parameters = std::wstring(L"/S /AllUsers /SetupWorker=") + ticket + L" /SetupConfig=\"" + configuration + L"\"";
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
    SHELLEXECUTEINFOW execute{sizeof(execute)};
    execute.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
    execute.hwnd = owner;
    execute.lpVerb = L"runas"; execute.lpFile = worker.c_str(); execute.lpParameters = parameters.c_str(); execute.nShow = SW_HIDE;
    DWORD result = ERROR_INSTALL_FAILURE;
    const auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (ShellExecuteExW(&execute)) {
        if (execute.hProcess) {
            WaitForSingleObject(execute.hProcess, INFINITE);
            GetExitCodeProcess(execute.hProcess, &result);
            CloseHandle(execute.hProcess);
        }
    } else { result = GetLastError(); if (result == ERROR_CANCELLED) result = ERROR_INSTALL_USEREXIT; }
    if (SUCCEEDED(initialized)) CoUninitialize();
    CloseHandle(event);
    if (uninstall) DeleteFileW(worker.c_str());
    return result;
}
