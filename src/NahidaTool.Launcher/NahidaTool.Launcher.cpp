#define _CRT_SECURE_NO_WARNINGS
#define NOMINMAX

#include <algorithm>
#include <filesystem>
#include <string>
#include <type_traits>
#include <utility>
#include <vector>
#include <Windows.h>

#pragma comment(linker, "/subsystem:windows /entry:wmainCRTStartup")

using namespace std::filesystem;

HANDLE stdOut;

template<typename T>
std::wstring ToStr(const T& val)
{
    if constexpr (std::is_same_v<T, std::wstring>)
        return val;
    else if constexpr (std::is_same_v<T, const wchar_t*> || std::is_same_v<T, wchar_t*>)
        return val;
    else if constexpr (std::is_same_v<T, const char*> || std::is_same_v<T, char*>)
        return std::wstring(val, val + strlen(val));
    else
        return std::to_wstring(val);
}

std::wstring FmtTime()
{
    SYSTEMTIME t;
    GetSystemTime(&t);
    wchar_t buf[32];
    swprintf_s(buf, L"[%02u:%02u:%02u.%03u] ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    return buf;
}

void Log(std::wstring output)
{
    if (stdOut)
    {
        std::wstring timeStr = FmtTime();
        WriteConsole(stdOut, timeStr.c_str(), static_cast<DWORD>(timeStr.length()), NULL, NULL);
        WriteConsole(stdOut, output.c_str(), static_cast<DWORD>(output.length()), NULL, NULL);
        WriteConsole(stdOut, L"\r\n", 2, NULL, NULL);
    }
}

void MoveTreeReplacing(const path& source, const path& destination)
{
    if (!exists(source))
        return;

    create_directories(destination.parent_path());
    if (!exists(destination))
    {
        rename(source, destination);
        return;
    }

    if (!is_directory(source) || !is_directory(destination))
    {
        remove_all(destination);
        rename(source, destination);
        return;
    }

    std::vector<path> entries;
    for (const auto& entry : directory_iterator(source))
        entries.push_back(entry.path());

    for (const auto& source_entry : entries)
    {
        path destination_entry = destination / source_entry.filename();
        if (is_directory(source_entry) && exists(destination_entry) && is_directory(destination_entry))
        {
            MoveTreeReplacing(source_entry, destination_entry);
            continue;
        }

        if (exists(destination_entry))
            remove_all(destination_entry);
        rename(source_entry, destination_entry);
    }

    remove(source);
}

bool MigrateServerSwitchResources(
    const path& base_folder,
    const path& target_folder,
    const std::vector<path>& old_app_folders)
{
    const path preservation_root = base_folder / L".update" / L"ServerSwitchCache";
    const path target_cache = target_folder / L"Assets" / L"ServerSwitchCache";

    try
    {
        // Recover data left by an interrupted previous update before collecting the current cache.
        if (exists(preservation_root))
        {
            Log(L"Recovering previously preserved server-switch resources");
            MoveTreeReplacing(preservation_root, target_cache);
        }

        // Merge older caches first so the most recent app version wins on duplicate files.
        for (const path& old_folder : old_app_folders)
        {
            path old_cache = old_folder / L"Assets" / L"ServerSwitchCache";
            if (!exists(old_cache))
                continue;

            Log(L"Preserving server-switch resources from: " + old_folder.filename().wstring());
            MoveTreeReplacing(old_cache, preservation_root);
        }
    }
    catch (const std::exception& ex)
    {
        Log(L"Preserve server-switch resources failed: " + ToStr(ex.what()));
        MessageBox(
            NULL,
            L"Failed to preserve the server-switch resources.\r\n"
            L"To prevent data loss, old app files were not deleted. Please restart NahidaTool and try again.",
            L"NahidaTool Update",
            MB_ICONERROR | MB_OK);
        return false;
    }

    for (const path& old_folder : old_app_folders)
    {
        Log(L"Removing old version: " + old_folder.filename().wstring());
        try
        {
            remove_all(old_folder);
        }
        catch (const std::exception& ex)
        {
            // The cache is already outside the old app folder, so a cleanup failure is safe to ignore.
            Log(L"Remove old version failed: " + ToStr(ex.what()));
        }
    }

    try
    {
        if (exists(preservation_root))
        {
            Log(L"Restoring server-switch resources into: " + target_folder.filename().wstring());
            MoveTreeReplacing(preservation_root, target_cache);
        }

        path update_root = preservation_root.parent_path();
        if (exists(update_root) && is_empty(update_root))
            remove(update_root);
    }
    catch (const std::exception& ex)
    {
        Log(L"Restore server-switch resources failed: " + ToStr(ex.what()));
        std::wstring message =
            L"Failed to restore the server-switch resources after updating.\r\n"
            L"Your data is still preserved at:\r\n" + preservation_root.wstring() +
            L"\r\n\r\nRestart NahidaTool to retry recovery.";
        MessageBox(NULL, message.c_str(), L"NahidaTool Update", MB_ICONERROR | MB_OK);
        return false;
    }

    return true;
}

int wmain(int argc, wchar_t* argv[])
{
    DWORD wait_pid = 0;
    std::wstring forwarded_args;
    for (int i = 1; i < argc; i++)
    {
        if (!wcscmp(argv[i], L"--wait-pid") && i + 1 < argc)
        {
            wait_pid = wcstoul(argv[++i], nullptr, 10);
            continue;
        }

        if (!wcscmp(argv[i], L"--trace"))
        {
            AllocConsole();
            continue;
        }

        if (!forwarded_args.empty())
            forwarded_args += L" ";
        forwarded_args += L"\"";
        for (wchar_t ch : std::wstring(argv[i]))
        {
            if (ch == L'"')
                forwarded_args += L'\\';
            forwarded_args += ch;
        }
        forwarded_args += L"\"";
    }

    stdOut = GetStdHandle(STD_OUTPUT_HANDLE);

    if (wait_pid != 0)
    {
        HANDLE wait_process = OpenProcess(SYNCHRONIZE, FALSE, wait_pid);
        if (wait_process)
        {
            WaitForSingleObject(wait_process, 120000);
            CloseHandle(wait_process);
        }
    }


    std::wstring run_exe;

    auto base_folder = path(argv[0]).parent_path();

    if (!run_exe.length())
    {
        path target_exe;
        file_time_type last_time = file_time_type::min();
        for (auto folder : directory_iterator(base_folder))
        {
            if (folder.is_directory())
            {
                auto exe = path(folder).append(L"NahidaTool.exe");
                if (exists(exe))
                {
                    auto time = last_write_time(exe);
                    if (time > last_time)
                    {
                        target_exe = exe;
                        last_time = time;
                    }
                }
            }
        }
        run_exe = target_exe.wstring();
    }

    Log(L"run_exe: " + run_exe);

    if (run_exe.length())
    {
        path target_folder = path(run_exe).parent_path();
        std::vector<std::pair<file_time_type, path>> old_app_candidates;
        for (const auto& folder : directory_iterator(base_folder))
        {
            std::wstring folder_name = folder.path().filename().wstring();
            // Portable packages start in "app"; staged self-updates use "app-<version>".
            bool is_app_folder = folder_name == L"app" || folder_name.starts_with(L"app-");
            if (!folder.is_directory() || !is_app_folder || folder.path() == target_folder)
                continue;

            path old_exe = folder.path() / L"NahidaTool.exe";
            file_time_type timestamp = exists(old_exe) ? last_write_time(old_exe) : file_time_type::min();
            old_app_candidates.emplace_back(timestamp, folder.path());
        }
        std::sort(old_app_candidates.begin(), old_app_candidates.end(),
            [](const auto& left, const auto& right) { return left.first < right.first; });

        std::vector<path> old_app_folders;
        old_app_folders.reserve(old_app_candidates.size());
        for (const auto& candidate : old_app_candidates)
            old_app_folders.push_back(candidate.second);

        if (!MigrateServerSwitchResources(base_folder, target_folder, old_app_folders))
            return 2;

        std::wstring arg = forwarded_args;
        Log(L"arg: " + arg);
        STARTUPINFO si;
        PROCESS_INFORMATION pi;
        ZeroMemory(&si, sizeof(si));
        si.cb = sizeof(si);
        ZeroMemory(&pi, sizeof(pi));
        Log(L"Starting process");
        if (!CreateProcess(run_exe.c_str(), arg.empty() ? nullptr : arg.data(), NULL, NULL, false, 0, NULL, NULL, &si, &pi))
        {
            Log(L"CreateProcess failed: " + ToStr(GetLastError()));
            return 1;
        }
        Log(L"Process started (" + ToStr(GetProcessId(pi.hProcess)) + L")");
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
    }
    else
    {
        Log(L"NahidaTool.exe not found");
        SetProcessDPIAware();
        auto ok = MessageBox(NULL, L"NahidaTool files not found.\r\nWould you like to download it now?\r\nhttps://github.com/IkunPS/NahidaTool", L"NahidaTool", MB_ICONWARNING | MB_OKCANCEL);
        if (ok == IDOK)
        {
            ShellExecute(NULL, NULL, L"https://github.com/IkunPS/NahidaTool", NULL, NULL, SW_SHOWNORMAL);
        }
    }

    if (stdOut)
    {
        Log(L"Wait for 10s to exit...");
        Sleep(10000);
    }

    return 0;
}
