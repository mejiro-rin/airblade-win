using System.Runtime.InteropServices;
using AirBlade.Models;

namespace AirBlade.Services;

/// <summary>
/// 拼接用于反馈问题的系统与应用诊断信息。
/// </summary>
public static class DiagnosticsInfo
{
    // RtlGetVersion 使用的操作系统版本信息结构。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OSVERSIONINFOEXW
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
    }

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    private static extern int RtlGetVersion(ref OSVERSIONINFOEXW versionInfo);

    /// <summary>
    /// 生成诊断信息文本,所有字段均有兜底值,不会出现空字符串。
    /// </summary>
    public static string Build()
    {
        var appVersion = typeof(DiagnosticsInfo).Assembly.GetName().Version?.ToString(3) ?? "未知";
        var windows = GetWindowsVersion();
        var runtime = RuntimeInformation.FrameworkDescription ?? "未知";
        var arch = RuntimeInformation.OSArchitecture.ToString();
        var language = LocalizationService.Current.Language == AppLanguage.English ? "English" : "中文";

        return string.Join(
            Environment.NewLine,
            $"AirBlade 版本:{appVersion}",
            $"Windows 版本:{windows}",
            $".NET 运行时:{runtime}",
            $"系统架构:{arch}",
            $"界面语言:{language}");
    }

    /// <summary>
    /// 用 RtlGetVersion 获取准确的 Windows 版本号;Environment.OSVersion 受清单影响可能返回旧值。
    /// </summary>
    private static string GetWindowsVersion()
    {
        var info = new OSVERSIONINFOEXW { dwOSVersionInfoSize = Marshal.SizeOf<OSVERSIONINFOEXW>() };
        if (RtlGetVersion(ref info) == 0)
        {
            return $"Windows {info.dwMajorVersion}.{info.dwMinorVersion} (Build {info.dwBuildNumber})";
        }

        return Environment.OSVersion.VersionString;
    }
}
