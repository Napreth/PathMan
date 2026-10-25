#include <windows.h>
#include <shellapi.h>
#include <objbase.h>
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

int wait_for_target(HANDLE process) {
    if (!process) return 0; // An existing associated application handled the file.
    SetConsoleCtrlHandler(handle_console_control, TRUE);
    const auto wait = WaitForSingleObject(process, INFINITE);
    DWORD exit_code = 1;
    if (wait != WAIT_OBJECT_0 || !GetExitCodeProcess(process, &exit_code)) {
        const auto error = GetLastError();
        CloseHandle(process);
        return fail(L"Cannot read target exit code", error);
    }
    CloseHandle(process);
    return static_cast<int>(exit_code);
}

int open_associated_file(const std::wstring& target) {
    const auto apartment = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    SHELLEXECUTEINFOW execution{};
    execution.cbSize = sizeof(execution);
    execution.fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI | SEE_MASK_NO_CONSOLE;
    execution.lpFile = target.c_str();
    auto parameters = argument_tail();
    while (*parameters == L' ' || *parameters == L'\t') ++parameters;
    execution.lpParameters = parameters;
    execution.nShow = SW_SHOWNORMAL;
    const bool opened = ShellExecuteExW(&execution) != FALSE;
    const auto error = opened ? ERROR_SUCCESS : GetLastError();
    if (SUCCEEDED(apartment)) CoUninitialize();
    return opened ? wait_for_target(execution.hProcess) : fail(L"Cannot open target file", error);
}

bool decode_target(const std::wstring& encoded, std::wstring& target) {
    if (encoded.size() % 4) return false;
    const std::wstring alphabet = L"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::vector<unsigned char> bytes;
    for (size_t i = 0; i < encoded.size(); i += 4) {
        unsigned value = 0;
        unsigned padding = 0;
        for (size_t j = 0; j < 4; ++j) {
            const auto c = encoded[i + j];
            if (c == L'=') {
                if (j < 2 || i + 4 != encoded.size()) return false;
                ++padding; value <<= 6;
            } else {
                const auto digit = alphabet.find(c);
                if (padding || digit == std::wstring::npos) return false;
                value = (value << 6) | static_cast<unsigned>(digit);
            }
        }
        bytes.push_back(static_cast<unsigned char>(value >> 16));
        if (padding < 2) bytes.push_back(static_cast<unsigned char>(value >> 8));
        if (padding == 0) bytes.push_back(static_cast<unsigned char>(value));
    }
    if (bytes.size() % 2 || bytes.size() / 2 >= 32768) return false;
    for (size_t i = 0; i < bytes.size(); i += 2) target.push_back(static_cast<wchar_t>(bytes[i] | (bytes[i + 1] << 8)));
    return !target.empty() && target.find(L'\0') == std::wstring::npos;
}
}

int wmain() {
    const auto executable = executable_path();
    if (executable.empty()) return fail(L"Cannot locate launcher", GetLastError());
    auto manifest = std::filesystem::path(executable);
    manifest.replace_extension(L".shim");
    const auto format = GetPrivateProfileIntW(L"PathMan.Shim", L"Format", 0, manifest.c_str());
    if (format != 1 && format != 2)
        return fail(L"Missing or unsupported .shim configuration", ERROR_INVALID_DATA);

    std::vector<wchar_t> buffer(131072);
    const auto length = GetPrivateProfileStringW(L"PathMan.Shim", format == 2 ? L"TargetBase64" : L"Target", L"", buffer.data(),
                                               static_cast<DWORD>(buffer.size()), manifest.c_str());
    if (length == 0 || length >= buffer.size() - 1)
        return fail(L"Invalid target", ERROR_INVALID_DATA);
    std::wstring target;
    if (format == 2) {
        if (!decode_target({buffer.data(), length}, target)) return fail(L"Invalid target", ERROR_INVALID_DATA);
    } else target.assign(buffer.data(), length);
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
                        nullptr, nullptr, &startup, &process)) {
        const auto error = GetLastError();
        if (error == ERROR_BAD_EXE_FORMAT) return open_associated_file(target);
        return fail(L"Cannot start target", error);
    }
    CloseHandle(process.hThread);
    return wait_for_target(process.hProcess);
}
