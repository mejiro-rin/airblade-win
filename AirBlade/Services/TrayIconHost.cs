using System.Runtime.InteropServices;
using WinRT.Interop;

namespace AirBlade.Services;

/// <summary>
/// 使用原生通知区域图标提供快捷窗入口，不依赖 Windows Forms。
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint CallbackMessage = WmApp + 1;
    private const uint WmNull = 0x0000;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmRightButtonUp = 0x0205;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const int TrayIconSize = 32;
    private const nuint SubclassId = 1;
    private const nuint OpenConsoleCommand = 1001;
    private const nuint OpenSettingsCommand = 1002;
    private const nuint ExitCommand = 1003;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const int SwShowNoActivate = 4;
    private const uint LwaAlpha = 0x00000002;
    private const uint WsPopup = 0x80000000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExToolWindow = 0x00000080;

    private static readonly SubclassProc Procedure = OnWindowMessage;
    private static readonly Dictionary<IntPtr, TrayIconHost> Hosts = [];
    private readonly IntPtr _windowHandle;
    private readonly Action _showQuickWindow;
    private readonly Action _showSettingsWindow;
    private readonly Action _exitApplication;
    private readonly IntPtr _menuHandle;
    private readonly IntPtr _menuHostHandle;
    private readonly IntPtr _iconHandle;
    private int _disposed;

    public TrayIconHost(Microsoft.UI.Xaml.Window window, Action showQuickWindow, Action showSettingsWindow, Action exitApplication)
    {
        ArgumentNullException.ThrowIfNull(window);
        _showQuickWindow = showQuickWindow ?? throw new ArgumentNullException(nameof(showQuickWindow));
        _showSettingsWindow = showSettingsWindow ?? throw new ArgumentNullException(nameof(showSettingsWindow));
        _exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));
        _windowHandle = WindowNative.GetWindowHandle(window);
        if (_windowHandle == IntPtr.Zero) throw new InvalidOperationException("无法获取快捷窗句柄。");
        lock (Hosts) Hosts[_windowHandle] = this;
        if (!SetWindowSubclass(_windowHandle, Procedure, SubclassId, UIntPtr.Zero))
            throw new InvalidOperationException("无法注册系统托盘消息处理。", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        _menuHandle = CreatePopupMenu();
        if (_menuHandle == IntPtr.Zero ||
            !AppendMenu(_menuHandle, MfString, OpenConsoleCommand, "打开控制台") ||
            !AppendMenu(_menuHandle, MfString, OpenSettingsCommand, "打开设置") ||
            !AppendMenu(_menuHandle, MfSeparator, 0, null) ||
            !AppendMenu(_menuHandle, MfString, ExitCommand, "退出"))
            throw new InvalidOperationException("无法创建 AirBlade 系统托盘菜单。");

        // 独立的全透明宿主窗口用于承载右键菜单：弹出菜单前必须把调用线程的窗口置为前台，
        // 但直接激活主窗口会把主窗口带到前台，这里改为激活宿主窗口，主窗口保持原状态。
        _menuHostHandle = CreateWindowEx(
            WsExLayered | WsExToolWindow,
            "STATIC",
            null,
            WsPopup,
            0, 0, 1, 1,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (_menuHostHandle == IntPtr.Zero)
            throw new InvalidOperationException("无法创建托盘菜单宿主窗口。");
        _ = SetLayeredWindowAttributes(_menuHostHandle, 0, 0, LwaAlpha);
        _ = ShowWindow(_menuHostHandle, SwShowNoActivate);

        _iconHandle = LoadImage(
            IntPtr.Zero,
            Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            ImageIcon,
            TrayIconSize,
            TrayIconSize,
            LrLoadFromFile);
        if (_iconHandle == IntPtr.Zero) throw new InvalidOperationException("无法加载 AirBlade 系统托盘图标。");
        var data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _windowHandle,
            Id = 1,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = CallbackMessage,
            IconHandle = _iconHandle,
            Tip = "AirBlade",
        };
        if (!ShellNotifyIcon(NimAdd, ref data)) throw new InvalidOperationException("无法创建 AirBlade 系统托盘图标。");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var data = new NotifyIconData { Size = (uint)Marshal.SizeOf<NotifyIconData>(), WindowHandle = _windowHandle, Id = 1 };
        _ = ShellNotifyIcon(NimDelete, ref data);
        if (_iconHandle != IntPtr.Zero) _ = DestroyIcon(_iconHandle);
        _ = RemoveWindowSubclass(_windowHandle, Procedure, SubclassId);
        if (_menuHandle != IntPtr.Zero) _ = DestroyMenu(_menuHandle);
        if (_menuHostHandle != IntPtr.Zero) _ = DestroyWindow(_menuHostHandle);
        lock (Hosts) Hosts.Remove(_windowHandle);
    }

    private static IntPtr OnWindowMessage(IntPtr windowHandle, uint message, UIntPtr wParam, IntPtr lParam, nuint subclassId, UIntPtr referenceData)
    {
        if (message == CallbackMessage)
        {
            TrayIconHost? host;
            lock (Hosts) Hosts.TryGetValue(windowHandle, out host);
            if (host is not null)
            {
                if ((uint)lParam.ToInt64() == WmLeftButtonUp) host._showQuickWindow();
                if ((uint)lParam.ToInt64() == WmRightButtonUp) host.ShowContextMenu();
            }
        }
        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (!GetCursorPos(out var point)) return;
        _ = ShowWindow(_menuHostHandle, SwShowNoActivate);
        _ = SetForegroundWindow(_menuHostHandle);
        // 用返回值获取选中的菜单项，命令不再经 WM_COMMAND 发给主窗口。
        var command = TrackPopupMenuEx(_menuHandle, TpmRightButton | TpmReturnCommand, point.X, point.Y, _menuHostHandle, IntPtr.Zero);
        // 菜单关闭后让焦点正确离开，否则点击外部时菜单不会正常消失。
        _ = PostMessage(_menuHostHandle, WmNull, UIntPtr.Zero, IntPtr.Zero);
        HandleMenuCommand(command);
    }

    private void HandleMenuCommand(IntPtr command)
    {
        if (command == IntPtr.Zero) return;
        switch ((nuint)command.ToInt64())
        {
            case OpenConsoleCommand: _showQuickWindow(); break;
            case OpenSettingsCommand: _showSettingsWindow(); break;
            case ExitCommand: _exitApplication(); break;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr SubclassProc(IntPtr windowHandle, uint message, UIntPtr wParam, IntPtr lParam, nuint subclassId, UIntPtr referenceData);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int desiredWidth, int desiredHeight, uint loadFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr windowHandle, SubclassProc procedure, nuint subclassId, UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr windowHandle, SubclassProc procedure, nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr windowHandle, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menuHandle, uint flags, nuint itemId, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menuHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr TrackPopupMenuEx(IntPtr menuHandle, uint flags, int x, int y, IntPtr windowHandle, IntPtr parameters);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string? windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr windowHandle, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr windowHandle, uint message, UIntPtr wParam, IntPtr lParam);
}
