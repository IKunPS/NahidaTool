#define _CRT_SECURE_NO_WARNINGS
#define NOMINMAX

#include <filesystem>
#include <string>
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

        auto base_name = path(run_exe).parent_path().filename().wstring();
        for (auto folder : directory_iterator(base_folder))
        {
            auto folder_name = folder.path().filename().wstring();
            if (folder.is_directory() && folder_name.starts_with(L"app-") && folder_name != base_name)
            {
                Log(L"Removing old version: " + folder_name);
                try
                {
                    remove_all(folder);
                }
                catch (const std::exception& ex)
                {
                    Log(L"Remove old version failed: " + ToStr(ex.what()));
                }
            }
        }
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
