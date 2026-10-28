#include "../packaging/native/verified-worker.h"
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>

namespace {
void check(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
void write(const std::filesystem::path& path, const std::string& value) {
    std::ofstream(path, std::ios::binary).write(value.data(), static_cast<std::streamsize>(value.size()));
}
}
int main() {
    const auto root = std::filesystem::temp_directory_path() / (L"PathMan-worker-" + std::to_wstring(GetCurrentProcessId()));
    try {
        std::filesystem::create_directory(root);
        const auto source = root / L"source.exe", worker = root / L"worker.exe";
        const std::string bytes(100003, 'x'); // Includes complete and partial read buffers.
        write(source, bytes); write(worker, bytes);
        {
            pathman_setup::verified_worker same(worker.c_str(), source.c_str());
            check(same.error() == ERROR_SUCCESS, "identical worker was rejected");
            const auto overwrite = CreateFileW(worker.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
            check(overwrite == INVALID_HANDLE_VALUE && GetLastError() == ERROR_SHARING_VIOLATION, "verified worker can be overwritten");
            check(!DeleteFileW(worker.c_str()) && GetLastError() == ERROR_SHARING_VIOLATION, "verified worker can be deleted");
        }
        {
            pathman_setup::verified_worker same(source.c_str(), source.c_str());
            check(same.error() == ERROR_SUCCESS, "same executable was rejected");
        }
        for (const auto offset : {size_t{0}, size_t{32768}, bytes.size() - 1}) {
            auto changed = bytes; changed[offset] = 'y'; write(worker, changed);
            pathman_setup::verified_worker different(worker.c_str(), source.c_str());
            check(different.error() == ERROR_FILE_CORRUPT, "changed worker was accepted");
        }
        write(worker, bytes.substr(1));
        { pathman_setup::verified_worker shorter(worker.c_str(), source.c_str()); check(shorter.error() == ERROR_FILE_CORRUPT, "truncated worker was accepted"); }
        write(worker, "");
        { pathman_setup::verified_worker empty(worker.c_str(), source.c_str()); check(empty.error() == ERROR_FILE_CORRUPT, "empty worker was accepted"); }
        { pathman_setup::verified_worker missing((root / L"missing.exe").c_str(), source.c_str()); check(missing.error() == ERROR_FILE_NOT_FOUND, "missing worker was accepted"); }
        check(DeleteFileW(worker.c_str()) != FALSE, "worker lock was not released");
        std::filesystem::remove(source);
        std::filesystem::remove(root);
        std::cout << "Worker identity and lifetime locks passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        std::error_code ignored;
        std::filesystem::remove(root / L"source.exe", ignored);
        std::filesystem::remove(root / L"worker.exe", ignored);
        std::filesystem::remove(root, ignored);
        return 1;
    }
}
