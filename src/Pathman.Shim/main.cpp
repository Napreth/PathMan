#include <windows.h>
#include <filesystem>
#include <iostream>
#include <string>
#include <vector>

namespace {
std::wstring executable_path() {
    std::vector<wchar_t> buffer(32768);
    const auto length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size()) return {};
    return {buffer.data(), length};
}

// Preserve the original argument tail, including all quoting and empty arguments.
// Windows parses the executable token specially: quotes toggle even after a backslash.
const wchar_t* argument_tail() {
    const wchar_t* cursor = GetCommandLineW();
    bool quoted = false;
    while (*cursor != L'\0') {
        if (*cursor == L'"') quoted = !quoted;
        else if (!quoted && (*cursor == L' ' || *cursor == L'\t')) break;
        ++cursor;
    }
    return cursor;
}

bool same_file(const std::wstring& left, const std::wstring& right) {
    const auto open = [](const std::wstring& path) {
        return CreateFileW(path.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    };
    const HANDLE a = open(left);
    const HANDLE b = open(right);
    BY_HANDLE_FILE_INFORMATION ai{}, bi{};
    const bool equal = a != INVALID_HANDLE_VALUE && b != INVALID_HANDLE_VALUE &&
        GetFileInformationByHandle(a, &ai) && GetFileInformationByHandle(b, &bi) &&
        ai.dwVolumeSerialNumber == bi.dwVolumeSerialNumber &&
        ai.nFileIndexHigh == bi.nFileIndexHigh && ai.nFileIndexLow == bi.nFileIndexLow;
    if (a != INVALID_HANDLE_VALUE) CloseHandle(a);
    if (b != INVALID_HANDLE_VALUE) CloseHandle(b);
    return equal;
}

BOOL WINAPI handle_console_control(DWORD event) {
    // The child receives Ctrl+C / Ctrl+Break itself; keep waiting to return its exit code.
    return event == CTRL_C_EVENT || event == CTRL_BREAK_EVENT;
}

std::wstring installed_shim() {
    for (const auto hive : {HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE})
    for (const auto view : {KEY_WOW64_32KEY, KEY_WOW64_64KEY}) {
        HKEY key = nullptr;
        if (RegOpenKeyExW(hive, L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\PathMan", 0, KEY_QUERY_VALUE | view, &key)) continue;
        DWORD bytes = 0;
        const auto flags = RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ;
        auto error = RegGetValueW(key, nullptr, L"InstallLocation", flags, nullptr, nullptr, &bytes);
        if (error || bytes == 0 || bytes > 65536) { RegCloseKey(key); continue; }
        std::vector<wchar_t> buffer(bytes / sizeof(wchar_t) + 1);
        if (!error) error = RegGetValueW(key, nullptr, L"InstallLocation", flags, nullptr, buffer.data(), &bytes);
        RegCloseKey(key);
        if (error || buffer.empty()) continue;
        const auto root = std::filesystem::path(buffer.data());
        if (!root.is_absolute()) continue;
        const auto shim = root / L"internal" / L"PathMan.Shim.exe";
        if (GetFileAttributesW(shim.c_str()) != INVALID_FILE_ATTRIBUTES) return shim.wstring();
    }
    return {};
}

int fail(const wchar_t* message, DWORD code) {
    std::wcerr << L"PathMan.Shim: " << message << L" (" << code << L")\n";
    return code == 0 ? 1 : static_cast<int>(code);
}
}

int wmain() {
    const auto executable = executable_path();
    if (executable.empty()) return fail(L"Cannot locate launcher", GetLastError());
    auto manifest = std::filesystem::path(executable);
    manifest.replace_extension(L".shim");
    if (GetPrivateProfileIntW(L"PathMan.Shim", L"Format", 0, manifest.c_str()) != 1)
        return fail(L"Missing or unsupported .shim configuration", ERROR_INVALID_DATA);

    std::vector<wchar_t> buffer(32768);
    const auto length = GetPrivateProfileStringW(L"PathMan.Shim", L"Target", L"", buffer.data(),
                                               static_cast<DWORD>(buffer.size()), manifest.c_str());
    if (length == 0 || length >= buffer.size() - 1)
        return fail(L"Invalid target", ERROR_INVALID_DATA);
    const std::wstring target(buffer.data(), length);
    if (!std::filesystem::path(target).is_absolute())
        return fail(L"Target must be an absolute path", ERROR_BAD_PATHNAME);
    const auto original = installed_shim();
    if (same_file(executable, target) || (!original.empty() && same_file(original, target)))
        return fail(L"Alias cannot launch a PathMan shim", ERROR_CIRCULAR_DEPENDENCY);

    std::wstring command = L"\"" + target + L"\"" + argument_tail();
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    GetStartupInfoW(&startup);
    PROCESS_INFORMATION process{};
    // Inherit the caller's environment, working directory and redirected standard handles.
    if (!CreateProcessW(target.c_str(), command.data(), nullptr, nullptr, TRUE, 0,
                        nullptr, nullptr, &startup, &process))
        return fail(L"Cannot start target", GetLastError());
    SetConsoleCtrlHandler(handle_console_control, TRUE);
    CloseHandle(process.hThread);
    const auto wait = WaitForSingleObject(process.hProcess, INFINITE);
    DWORD exit_code = 1;
    if (wait != WAIT_OBJECT_0 || !GetExitCodeProcess(process.hProcess, &exit_code)) {
        const auto error = GetLastError();
        CloseHandle(process.hProcess);
        return fail(L"Cannot read target exit code", error);
    }
    CloseHandle(process.hProcess);
    return static_cast<int>(exit_code);
}
