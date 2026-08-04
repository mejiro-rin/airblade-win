using Microsoft.Win32;

namespace AirBlade.Services;

/// <summary>
/// 通过 HKCU 注册表 Run 键实现开机自启，写入当前用户即可，不需要管理员权限。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AirBlade";

    /// <summary>
    /// 按开关状态同步开机自启：开启时写入当前可执行文件路径，关闭时删除注册表项。
    /// </summary>
    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                // Run 键的值按命令行解析，路径含空格时必须加引号，否则登录启动会失败。
                key.SetValue(ValueName, $"\"{executablePath}\"");
            }
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
