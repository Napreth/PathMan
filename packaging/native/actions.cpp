#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <tlhelp32.h>
#include <aclapi.h>
#include <filesystem>
#include <string>
#include <vector>
#include <cwctype>

#ifdef _M_IX86
#pragma comment(linker, "/EXPORT:ValidateDirectory=_ValidateDirectory@8")
#pragma comment(linker, "/EXPORT:HasDesktopRuntime=_HasDesktopRuntime@4")
#pragma comment(linker, "/EXPORT:StopApplications=_StopApplications@4")
#pragma comment(linker, "/EXPORT:ConfigureInstall=_ConfigureInstall@8")
#pragma comment(linker, "/EXPORT:CleanupInstall=_CleanupInstall@12")
#pragma comment(linker, "/EXPORT:RefreshShims=_RefreshShims@8")
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
}
extern "C" __declspec(dllexport) DWORD __stdcall ValidateDirectory(const wchar_t* directory, const wchar_t* localData) {
    try {
        const std::wstring path(directory);
        if (!std::filesystem::path(path).is_absolute() || path.find_first_of(L"\"<>|?*") != std::wstring::npos) return ERROR_BAD_PATHNAME;
        const auto target = volume(path);
        if (target.empty() || target != volume(localData)) return ERROR_NOT_SAME_DEVICE;
        DWORD flags = 0;
        if (!GetVolumeInformationW(target.c_str(), nullptr, 0, nullptr, nullptr, &flags, nullptr, 0) || !(flags & FILE_SUPPORTS_HARD_LINKS)) return ERROR_NOT_SUPPORTED;
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
    const std::filesystem::path root(directory);
    const auto snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return GetLastError();
    PROCESSENTRY32W entry{sizeof(entry)};
    DWORD result = ERROR_SUCCESS;
    if (Process32FirstW(snapshot, &entry)) do {
        const bool gui = _wcsicmp(entry.szExeFile, L"PathMan.exe") == 0;
        if (!gui && _wcsicmp(entry.szExeFile, L"PathMan.Service.exe") != 0) continue;
        const auto process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID);
        if (!process) continue;
        wchar_t path[32768]{}; DWORD length = 32768;
        const auto expected = gui ? root / L"PathMan.exe" : root / L"internal" / L"PathMan.Service.exe";
        if (QueryFullProcessImageNameW(process, 0, path, &length) && _wcsicmp(path, expected.c_str()) == 0) {
            const auto terminable = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, entry.th32ProcessID);
            if (!terminable) result = GetLastError();
            else {
                if (!TerminateProcess(terminable, 0) || WaitForSingleObject(terminable, 5000) != WAIT_OBJECT_0) result = ERROR_BUSY;
                CloseHandle(terminable);
            }
        }
        CloseHandle(process);
    } while (Process32NextW(snapshot, &entry));
    CloseHandle(snapshot); return result;
}
extern "C" __declspec(dllexport) DWORD __stdcall ConfigureInstall(const wchar_t* directory, BOOL machine) {
    try {
        const std::filesystem::path root(directory);
        auto error = machine ? allow_links(root / L"internal" / L"PathMan.Shim.exe") : ERROR_SUCCESS;
        if (!error) error = change_path((root / L"bin").wstring(), machine != FALSE, true, L"BinPathOwned");
        if (!error && !machine) error = change_path(folder(FOLDERID_LocalAppData) + L"\\PathMan\\shims", false, true, L"ShimsPathOwned");
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
        constexpr auto run = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const auto expected = L"\"" + (root / L"internal" / L"PathMan.Service.exe").wstring() + L"\"";
        if (_wcsicmp(read(HKEY_CURRENT_USER, run, L"PathMan").c_str(), expected.c_str()) == 0) {
            HKEY opened = nullptr;
            if (!RegOpenKeyExW(HKEY_CURRENT_USER, run, 0, KEY_SET_VALUE, &opened)) { RegDeleteValueW(opened, L"PathMan"); RegCloseKey(opened); }
        }
        remove_shortcut(std::filesystem::path(desktop) / L"PathMan.lnk", root / L"PathMan.exe");
        return error;
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}
extern "C" __declspec(dllexport) DWORD __stdcall RefreshShims(const wchar_t* directory, const wchar_t* localData) {
    try {
        const auto source = std::filesystem::path(directory) / L"internal" / L"PathMan.Shim.exe";
        const auto aliases = std::filesystem::path(localData) / L"PathMan" / L"shims";
        if (!std::filesystem::exists(aliases)) return ERROR_SUCCESS;
        for (const auto& entry : std::filesystem::directory_iterator(aliases)) {
            if (entry.path().extension() != L".shim" || !entry.is_regular_file() || entry.is_symlink()) continue;
            if (GetPrivateProfileIntW(L"PathMan.Shim", L"Format", 0, entry.path().c_str()) != 1) continue;
            auto target = entry.path(); target.replace_extension(L".exe");
            wchar_t temporary[32768]{};
            if (!GetTempFileNameW(aliases.c_str(), L"pmi", 0, temporary)) return GetLastError();
            DeleteFileW(temporary);
            if (!CreateHardLinkW(temporary, source.c_str(), nullptr)) return GetLastError();
            if (!MoveFileExW(temporary, target.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) { const auto error = GetLastError(); DeleteFileW(temporary); return error; }
        }
        return ERROR_SUCCESS;
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}
