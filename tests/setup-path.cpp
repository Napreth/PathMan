#include <windows.h>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>

extern "C" __declspec(dllimport) DWORD __stdcall ConfigureInstall(const wchar_t*, BOOL);
extern "C" __declspec(dllimport) DWORD __stdcall CleanupInstall(const wchar_t*, BOOL, const wchar_t*);

namespace {
void check(bool value, const char* message) {
    if (!value) throw std::runtime_error(message);
}
void success(LSTATUS status) {
    check(status == ERROR_SUCCESS, "Registry operation failed");
}
struct registry_fixture {
    std::wstring name = L"Software\\PathMan.Setup.Tests\\" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
    HKEY original = nullptr, root = nullptr;
    bool redirected = false;
    registry_fixture() {
        success(RegOpenCurrentUser(KEY_ALL_ACCESS, &original));
        success(RegCreateKeyExW(original, name.c_str(), 0, nullptr, 0, KEY_ALL_ACCESS, nullptr, &root, nullptr));
        success(RegOverridePredefKey(HKEY_CURRENT_USER, root));
        redirected = true;
    }
    ~registry_fixture() {
        if (redirected) RegOverridePredefKey(HKEY_CURRENT_USER, nullptr);
        if (root) RegCloseKey(root);
        if (original) {
            RegDeleteTreeW(original, name.c_str());
            RegCloseKey(original);
        }
    }
};
void set(const wchar_t* key, const wchar_t* name, const std::wstring& value, DWORD type = REG_EXPAND_SZ) {
    HKEY opened = nullptr;
    success(RegCreateKeyExW(HKEY_CURRENT_USER, key, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &opened, nullptr));
    const auto status = RegSetValueExW(opened, name, 0, type, reinterpret_cast<const BYTE*>(value.c_str()), static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(opened);
    success(status);
}
std::wstring path(DWORD* type = nullptr) {
    DWORD size = 0;
    success(RegGetValueW(HKEY_CURRENT_USER, L"Environment", L"Path", RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_NOEXPAND, type, nullptr, &size));
    std::vector<wchar_t> buffer(size / sizeof(wchar_t) + 1);
    success(RegGetValueW(HKEY_CURRENT_USER, L"Environment", L"Path", RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_NOEXPAND, type, buffer.data(), &size));
    return buffer.data();
}
constexpr auto directory = L"C:\\PathMan.Setup.Fixture";
constexpr auto bin = L"C:\\PathMan.Setup.Fixture\\bin";
void install() { check(ConfigureInstall(directory, FALSE) == ERROR_SUCCESS, "ConfigureInstall failed"); }
void uninstall() { check(CleanupInstall(directory, FALSE, L"C:\\PathMan.Setup.Fixture\\desktop") == ERROR_SUCCESS, "CleanupInstall failed"); }
}

int main() {
    try {
        registry_fixture fixture;
        const std::vector<std::wstring> values = {
            L"", L";", L";;", L"C:\\tools", L"C:\\tools;", L"C:\\tools;;",
            L";C:\\tools;;C:\\other;", L"%USERPROFILE%\\bin;", std::wstring(5000, L'x') + L";;"
        };
        for (const auto type : {REG_SZ, REG_EXPAND_SZ}) {
            for (const auto& value : values) {
                RegDeleteTreeW(HKEY_CURRENT_USER, L"Software\\PathMan");
                set(L"Environment", L"Path", value, type);
                install();
                const auto installed = path();
                check(installed == value, "Installation must not register the CLI or rewrite PATH");
                HKEY observed = nullptr;
                success(RegOpenKeyExW(HKEY_CURRENT_USER, L"Environment", 0, KEY_NOTIFY, &observed));
                const auto changed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
                check(changed != nullptr, "Could not create registry notification event");
                success(RegNotifyChangeKeyValue(observed, FALSE, REG_NOTIFY_CHANGE_LAST_SET, changed, TRUE));
                install();
                const auto notification = WaitForSingleObject(changed, 0);
                RegCloseKey(observed); CloseHandle(changed);
                check(notification == WAIT_TIMEOUT, "An unchanged reinstall must not write any environment registry value");
                check(path() == installed, "Reinstall must leave PATH unchanged");
                uninstall();
                DWORD actual_type = 0;
                check(path(&actual_type) == value && actual_type == type, "Uninstall must restore the exact original PATH and registry type");
            }
        }
        RegDeleteTreeW(HKEY_CURRENT_USER, L"Software\\PathMan");
        set(L"Environment", L"Path", std::wstring(bin) + L";;");
        install(); uninstall();
        check(path() == std::wstring(bin) + L";;", "A preexisting CLI entry must survive uninstall");
        set(L"Environment", L"SOFTWARE_PATH", std::wstring(bin) + L";C:\\other");
        set(L"Environment", L"Path", L"%SOFTWARE_PATH%;");
        install(); uninstall();
        check(path() == L"%SOFTWARE_PATH%;", "A CLI directory already covered by a reference must not be appended or removed");
        set(L"Environment", L"Path", std::wstring(bin) + L";;%SOFTWARE_PATH%;C:\\tail;");
        set(L"Software\\PathMan", L"BinPathOwned", L"unused");
        HKEY tracking = nullptr;
        success(RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\PathMan", 0, KEY_SET_VALUE, &tracking));
        const DWORD owned = 1;
        success(RegSetValueExW(tracking, L"BinPathOwned", 0, REG_DWORD, reinterpret_cast<const BYTE*>(&owned), sizeof(owned)));
        RegCloseKey(tracking);
        HKEY observed = nullptr;
        success(RegOpenKeyExW(HKEY_CURRENT_USER, L"Environment", 0, KEY_NOTIFY, &observed));
        const auto changed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        check(changed != nullptr, "Could not create registry notification event");
        success(RegNotifyChangeKeyValue(observed, FALSE, REG_NOTIFY_CHANGE_LAST_SET, changed, TRUE));
        install();
        check(WaitForSingleObject(changed, 0) == WAIT_TIMEOUT, "An owned direct entry plus a covering reference must not trigger a PATH write");
        RegCloseKey(observed); CloseHandle(changed);
        check(path() == std::wstring(bin) + L";;%SOFTWARE_PATH%;C:\\tail;", "Install must not repair, remove or normalize an existing CLI entry");
        std::cout << "PASS: isolated setup PATH preservation, empty entries, trailing separators, long values, registry types, reinstall ownership and referenced CLI directories.\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
