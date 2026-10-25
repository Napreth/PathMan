// Disposable-VM fixture only. Its copies exercise exact ETW process identity.
#include <windows.h>
#include <string>
#include <cwchar>
int wmain(int argc, wchar_t** argv) {
    if (argc == 2 && std::wcscmp(argv[1], L"--idle") == 0) { Sleep(60000); return 0; }
    const int offset = argc > 1 && std::wstring(argv[1]).find(L',') != std::wstring::npos ? 2 : 1;
    if (argc != offset + 2) return 2;
    HKEY key = nullptr;
    auto error = RegCreateKeyExW(HKEY_CURRENT_USER, L"Environment", 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr);
    if (error) return error;
    if (std::wcscmp(argv[1], L"--delete") == 0) { error = RegDeleteValueW(key, argv[2]); RegCloseKey(key); return error; }
    const std::wstring value(argv[offset + 1]);
    error = RegSetValueExW(key, argv[offset], 0, REG_EXPAND_SZ, reinterpret_cast<const BYTE*>(value.c_str()), static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return error;
}
