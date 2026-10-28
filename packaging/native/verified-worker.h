#pragma once
#include <windows.h>
#include <array>
#include <cstring>

namespace pathman_setup {
// NSIS keeps its CRC-checked executable open with FILE_SHARE_READ. Only launch
// an identical, read-locked worker. This also covers the uninstaller's temporary
// copy, without depending on NSIS's payload or checksum format.
class verified_worker {
    HANDLE file_ = INVALID_HANDLE_VALUE;
    DWORD error_ = ERROR_SUCCESS;
public:
    verified_worker(const wchar_t* executable, const wchar_t* verified_executable) {
        file_ = CreateFileW(executable, GENERIC_READ, FILE_SHARE_READ, nullptr,
                            OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
        if (file_ == INVALID_HANDLE_VALUE) { error_ = GetLastError(); return; }
        const auto source = CreateFileW(verified_executable, GENERIC_READ, FILE_SHARE_READ,
                                       nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
        if (source == INVALID_HANDLE_VALUE) { error_ = GetLastError(); return; }
        LARGE_INTEGER size{}, source_size{};
        if (!GetFileSizeEx(file_, &size) || !GetFileSizeEx(source, &source_size)) error_ = GetLastError();
        else if (!size.QuadPart || size.QuadPart != source_size.QuadPart) error_ = ERROR_FILE_CORRUPT;
        else {
            std::array<BYTE, 32768> actual{}, expected{};
            for (;;) {
                DWORD read = 0, source_read = 0;
                if (!ReadFile(file_, actual.data(), static_cast<DWORD>(actual.size()), &read, nullptr) ||
                    !ReadFile(source, expected.data(), static_cast<DWORD>(expected.size()), &source_read, nullptr)) {
                    error_ = GetLastError(); break;
                }
                if (read != source_read || std::memcmp(actual.data(), expected.data(), read)) {
                    error_ = ERROR_FILE_CORRUPT; break;
                }
                if (!read) break;
            }
        }
        CloseHandle(source);
    }
    ~verified_worker() { if (file_ != INVALID_HANDLE_VALUE) CloseHandle(file_); }
    verified_worker(const verified_worker&) = delete;
    verified_worker& operator=(const verified_worker&) = delete;
    DWORD error() const { return error_; }
};
}
