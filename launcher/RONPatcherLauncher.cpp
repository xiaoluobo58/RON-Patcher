#define UNICODE
#define _UNICODE
#include <windows.h>
#include <shellapi.h>
#include <string>

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    wchar_t modulePath[MAX_PATH]{};
    if (GetModuleFileNameW(nullptr, modulePath, MAX_PATH) == 0)
        return 1;

    std::wstring root(modulePath);
    const auto separator = root.find_last_of(L"\\/");
    if (separator == std::wstring::npos)
        return 1;
    root.resize(separator);

    const std::wstring executable = root + L"\\app\\RONPatcher.exe";
    if (GetFileAttributesW(executable.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        MessageBoxW(nullptr,
            L"找不到 app\\RONPatcher.exe。\n\n请重新解压完整安装包，不要只复制启动程序。",
            L"RON Patcher", MB_OK | MB_ICONERROR);
        return 2;
    }

    const auto result = reinterpret_cast<INT_PTR>(ShellExecuteW(
        nullptr, L"open", executable.c_str(), nullptr, root.c_str(), SW_SHOWNORMAL));
    if (result <= 32)
    {
        MessageBoxW(nullptr, L"RON Patcher 启动失败，请重新解压完整安装包。",
            L"RON Patcher", MB_OK | MB_ICONERROR);
        return 3;
    }
    return 0;
}
