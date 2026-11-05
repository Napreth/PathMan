#pragma once
#include <windows.h>
#include <aclapi.h>
#include <array>
#include <filesystem>
#include <vector>

namespace pathman_setup {
inline bool trusted_owner(PSID sid) {
    if (!sid) return false;
    if (IsWellKnownSid(sid, WinLocalSystemSid) || IsWellKnownSid(sid, WinBuiltinAdministratorsSid)) return true;
    std::array<BYTE, SECURITY_MAX_SID_SIZE> installer{};
    wchar_t domain[256]{}; DWORD bytes = static_cast<DWORD>(installer.size()), length = 256; SID_NAME_USE use{};
    return LookupAccountNameW(nullptr, L"NT SERVICE\\TrustedInstaller", installer.data(), &bytes, domain, &length, &use) && EqualSid(sid, installer.data());
}
inline bool protected_descriptor(PSECURITY_DESCRIPTOR security, bool payload) {
    PSID owner = nullptr; BOOL defaulted = FALSE, present = FALSE; PACL acl = nullptr;
    if (!GetSecurityDescriptorOwner(security, &owner, &defaulted) || !trusted_owner(owner) ||
        !GetSecurityDescriptorDacl(security, &present, &acl, &defaulted) || !present || !acl) return false;
    const DWORD dangerous = 0x500d0152u | (payload ? 4u : 0u);
    for (DWORD index = 0; index < acl->AceCount; ++index) {
        void* entry = nullptr; if (!GetAce(acl, index, &entry)) return false;
        const auto header = static_cast<ACE_HEADER*>(entry);
        if (header->AceFlags & INHERIT_ONLY_ACE) continue;
        if (header->AceType == ACCESS_DENIED_ACE_TYPE) continue;
        if (header->AceType != ACCESS_ALLOWED_ACE_TYPE) return false;
        const auto allow = static_cast<ACCESS_ALLOWED_ACE*>(entry);
        if ((allow->Mask & dangerous) && !trusted_owner(&allow->SidStart)) return false;
    }
    return true;
}
class protected_source {
    std::vector<HANDLE> handles_;
    DWORD error_ = ERROR_SUCCESS;
    bool open(const std::filesystem::path& path, bool directory, bool payload) {
        const auto handle = CreateFileW(path.c_str(), directory ? READ_CONTROL | FILE_READ_ATTRIBUTES : GENERIC_READ | READ_CONTROL,
            directory ? FILE_SHARE_READ | FILE_SHARE_WRITE : FILE_SHARE_READ, nullptr, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | (directory ? FILE_FLAG_BACKUP_SEMANTICS : 0), nullptr);
        if (handle == INVALID_HANDLE_VALUE) { error_ = GetLastError(); return false; }
        handles_.push_back(handle);
        BY_HANDLE_FILE_INFORMATION info{};
        if (!GetFileInformationByHandle(handle, &info)) { error_ = GetLastError(); return false; }
        if (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) { error_ = ERROR_ACCESS_DENIED; return false; }
        PSID owner = nullptr; PACL acl = nullptr; PSECURITY_DESCRIPTOR security = nullptr;
        error_ = GetSecurityInfo(handle, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, &owner, nullptr, &acl, nullptr, &security);
        if (error_) return false;
        const bool valid = protected_descriptor(security, payload); LocalFree(security);
        if (!valid) error_ = ERROR_ACCESS_DENIED;
        return valid;
    }
public:
    explicit protected_source(const std::filesystem::path& installation) {
        const auto root = installation.root_path();
        if (!installation.is_absolute() || GetDriveTypeW(root.c_str()) != DRIVE_FIXED) { error_ = ERROR_ACCESS_DENIED; return; }
        std::vector<std::filesystem::path> paths;
        for (auto path = installation / L"internal";; path = path.parent_path()) {
            paths.push_back(path); if (path == root) break;
            if (path.empty() || path == path.parent_path()) { error_ = ERROR_BAD_PATHNAME; return; }
        }
        for (auto it = paths.rbegin(); it != paths.rend(); ++it)
            if (!open(*it, true, *it == installation || *it == installation / L"internal")) return;
        open(installation / L"internal" / L"PathMan.Service.exe", false, true);
    }
    ~protected_source() { for (const auto handle : handles_) CloseHandle(handle); }
    protected_source(const protected_source&) = delete;
    protected_source& operator=(const protected_source&) = delete;
    DWORD error() const { return error_; }
};
}
