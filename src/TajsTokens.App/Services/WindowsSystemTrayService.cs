using System.ComponentModel;
using System.Runtime.InteropServices;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.App.Services;

/// <summary>
/// Native notification-area surface for Phase 2. Using Shell_NotifyIcon directly avoids pulling the
/// WPF/WindowsDesktop XAML toolchain into the WinUI project just to obtain WinForms NotifyIcon.
/// </summary>
public sealed class WindowsSystemTrayService : ISystemTrayService
{
    private const uint TrayIconId = 1;
    private const uint TrayCallbackMessage = WmApp + 1;
    private const uint CommandOpen = 1001;
    private const uint CommandRefresh = 1002;
    private const uint CommandNotifications = 1003;
    private const uint CommandStartup = 1004;
    private const uint CommandExit = 1005;

    private readonly WindowProc _windowProc;
    private readonly string _windowClassName = $"TajsTokens.Tray.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private nint _windowHandle;
    private nint _instanceHandle;
    private nint _iconHandle;
    private bool _notificationsEnabled;
    private bool _launchAtLogin;
    private bool _disposed;

    public WindowsSystemTrayService()
    {
        _windowProc = HandleWindowMessage;
    }

    public event EventHandler? OpenDashboardRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? ExitRequested;
    public event Action<bool>? NotificationsEnabledChanged;
    public event Action<bool>? LaunchAtLoginChanged;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowHandle != 0)
        {
            return;
        }

        _instanceHandle = GetModuleHandleW(null);
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            WindowProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
            Instance = _instanceHandle,
            ClassName = _windowClassName
        };

        if (RegisterClassExW(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the TajsTokens tray window class.");
        }

        _windowHandle = CreateWindowExW(
            0,
            _windowClassName,
            "TajsTokens tray host",
            0,
            0,
            0,
            0,
            0,
            HwndMessage,
            0,
            _instanceHandle,
            0);

        if (_windowHandle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            _ = UnregisterClassW(_windowClassName, _instanceHandle);
            throw new Win32Exception(error, "Could not create the TajsTokens tray message window.");
        }

        _iconHandle = LoadIconW(0, IdiApplication);
        var data = CreateNotifyIconData(NifMessage | NifIcon | NifTip);
        data.CallbackMessage = TrayCallbackMessage;
        data.Icon = _iconHandle;
        data.Tip = "TajsTokens · waiting for telemetry";

        if (!Shell_NotifyIconW(NimAdd, ref data))
        {
            DestroyTrayHost();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not add the TajsTokens notification-area icon.");
        }
    }

    public void UpdateStatus(SystemTrayStatus status)
    {
        if (_windowHandle == 0 || _disposed)
        {
            return;
        }

        var constrained = status.ConstrainedRemainingPercent is int percent ? $" · constrained {percent}%" : string.Empty;
        var data = CreateNotifyIconData(NifTip);
        data.Tip = Truncate(status.Tooltip + constrained, 127);
        _ = Shell_NotifyIconW(NimModify, ref data);
    }

    public void UpdatePreferences(bool notificationsEnabled, bool launchAtLogin)
    {
        _notificationsEnabled = notificationsEnabled;
        _launchAtLogin = launchAtLogin;
    }

    public void ShowNotification(string title, string message)
    {
        if (_windowHandle == 0 || _disposed)
        {
            return;
        }

        var data = CreateNotifyIconData(NifInfo);
        data.InfoTitle = Truncate(title, 63);
        data.Info = Truncate(message, 255);
        data.InfoFlags = NiifInfo;
        _ = Shell_NotifyIconW(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_windowHandle != 0)
        {
            var data = CreateNotifyIconData(0);
            _ = Shell_NotifyIconW(NimDelete, ref data);
        }

        DestroyTrayHost();
    }

    private nint HandleWindowMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == TrayCallbackMessage)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64());
            if (mouseMessage == WmLButtonDblClk)
            {
                OpenDashboardRequested?.Invoke(this, EventArgs.Empty);
                return 0;
            }

            if (mouseMessage is WmRButtonUp or WmContextMenu)
            {
                ShowContextMenu();
                return 0;
            }
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (_windowHandle == 0)
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            _ = AppendMenuW(menu, MfString, CommandOpen, "Open TajsTokens");
            _ = AppendMenuW(menu, MfString, CommandRefresh, "Refresh telemetry");
            _ = AppendMenuW(menu, MfSeparator, 0, null);
            _ = AppendMenuW(menu, MfString | (_notificationsEnabled ? MfChecked : MfUnchecked), CommandNotifications, "Notifications enabled");
            _ = AppendMenuW(menu, MfString | (_launchAtLogin ? MfChecked : MfUnchecked), CommandStartup, "Start with Windows");
            _ = AppendMenuW(menu, MfSeparator, 0, null);
            _ = AppendMenuW(menu, MfString, CommandExit, "Exit");

            if (!GetCursorPos(out var point))
            {
                return;
            }

            _ = SetForegroundWindow(_windowHandle);
            var command = TrackPopupMenu(
                menu,
                TpmRightButton | TpmReturnCmd | TpmNoNotify,
                point.X,
                point.Y,
                0,
                _windowHandle,
                0);

            switch (command)
            {
                case CommandOpen:
                    OpenDashboardRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandRefresh:
                    RefreshRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandNotifications:
                    NotificationsEnabledChanged?.Invoke(!_notificationsEnabled);
                    break;
                case CommandStartup:
                    LaunchAtLoginChanged?.Invoke(!_launchAtLogin);
                    break;
                case CommandExit:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private NotifyIconData CreateNotifyIconData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _windowHandle,
        Id = TrayIconId,
        Flags = flags
    };

    private void DestroyTrayHost()
    {
        if (_windowHandle != 0)
        {
            _ = DestroyWindow(_windowHandle);
            _windowHandle = 0;
        }

        if (_instanceHandle != 0)
        {
            _ = UnregisterClassW(_windowClassName, _instanceHandle);
            _instanceHandle = 0;
        }

        _iconHandle = 0; // LoadIcon returns a shared system icon; it must not be destroyed.
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 1)] + "…";

    private delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private const uint WmApp = 0x8000;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;
    private const uint MfString = 0x00000000;
    private const uint MfChecked = 0x00000008;
    private const uint MfUnchecked = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNoNotify = 0x0080;
    private const uint TpmReturnCmd = 0x0100;
    private static readonly nint HwndMessage = new(-3);
    private static readonly nint IdiApplication = new(32512);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIconW(nint instance, nint iconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint itemId, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint window,
        nint rectangle);
}
