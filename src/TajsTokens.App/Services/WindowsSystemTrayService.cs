using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.App.Services;

/// <summary>
/// Native notification-area surface. Shell_NotifyIcon owns the tray lifecycle while a small hidden
/// Win32 window receives callbacks and Explorer's TaskbarCreated recovery broadcast.
/// </summary>
public sealed class WindowsSystemTrayService : ISystemTrayService
{
    private const uint TrayIconId = 1;
    private const uint TrayCallbackMessage = WmApp + 1;
    private const uint ApplyStatusMessage = WmApp + 2;
    private const uint CommandOpen = 1001;
    private const uint CommandRefresh = 1002;
    private const uint CommandNotifications = 1003;
    private const uint CommandStartup = 1004;
    private const uint CommandExit = 1005;

    private readonly WindowProc _windowProc;
    private readonly string _windowClassName = $"TajsTokens.Tray.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly Dictionary<StatusIconKey, Icon> _statusIconCache = [];
    private readonly HashSet<StatusIconKey> _pendingIconKeys = [];
    private readonly object _statusIconSync = new();
    private nint _windowHandle;
    private nint _instanceHandle;
    private nint _sharedFallbackIconHandle;
    private StatusIconKey? _desiredIconKey;
    private StatusIconKey? _currentIconKey;
    private uint _taskbarCreatedMessage;
    private string _currentTip = "TajsTokens · waiting for telemetry";
    private string? _lastAppliedTip;
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
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
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

        // A hidden top-level window, rather than HWND_MESSAGE, is intentional: Explorer broadcasts
        // TaskbarCreated only to top-level windows after the taskbar is recreated.
        _windowHandle = CreateWindowExW(
            0,
            _windowClassName,
            "TajsTokens tray host",
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            _instanceHandle,
            0);

        if (_windowHandle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            _ = UnregisterClassW(_windowClassName, _instanceHandle);
            throw new Win32Exception(error, "Could not create the TajsTokens tray window.");
        }

        _sharedFallbackIconHandle = LoadIconW(0, IdiApplication);
        if (!TryAddTrayIcon())
        {
            var error = Marshal.GetLastWin32Error();
            DestroyTrayHost();
            throw new Win32Exception(error, "Could not add the TajsTokens notification-area icon.");
        }
    }

    public void UpdateStatus(SystemTrayStatus status)
    {
        int? normalizedPercent = status.ConstrainedRemainingPercent is int percent
            ? Math.Clamp(percent, 0, 100)
            : null;
        var constrained = normalizedPercent is int value ? $" · constrained {value}%" : string.Empty;
        var nextTip = Truncate(status.Tooltip + constrained, 127);
        var nextKey = new StatusIconKey(normalizedPercent, status.IsFresh);

        Icon? readyIcon = null;
        var scheduleRender = false;
        lock (_statusIconSync)
        {
            if (_windowHandle == 0 || _disposed)
            {
                return;
            }

            // Keep desired state independently of Shell_NotifyIcon success. If Explorer is between
            // taskbars, TaskbarCreated must re-add the newest tooltip rather than the last one that
            // happened to modify successfully.
            _currentTip = nextTip;
            _desiredIconKey = nextKey;

            if (_currentIconKey == nextKey && string.Equals(_lastAppliedTip, nextTip, StringComparison.Ordinal))
            {
                return;
            }

            if (_statusIconCache.TryGetValue(nextKey, out readyIcon))
            {
                // Cache hits are cheap and can be applied immediately on the window thread.
            }
            else if (_pendingIconKeys.Add(nextKey))
            {
                scheduleRender = true;
            }
        }

        if (readyIcon is not null)
        {
            ApplyStatusIcon(nextKey, nextTip, readyIcon);
        }

        if (scheduleRender)
        {
            QueueStatusIconRender(nextKey);
        }
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

        lock (_statusIconSync)
        {
            foreach (var icon in _statusIconCache.Values)
            {
                icon.Dispose();
            }
            _statusIconCache.Clear();
            _pendingIconKeys.Clear();
            _desiredIconKey = null;
            _currentIconKey = null;
        }

        DestroyTrayHost();
    }

    private nint HandleWindowMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            // Explorer discarded all notification-area registrations. Re-add ours with the newest
            // desired tooltip and a ready cached badge when available. A pending render will post the
            // apply message when it completes.
            _ = TryAddTrayIcon();
            return 0;
        }

        if (message == ApplyStatusMessage)
        {
            ApplyDesiredStatus();
            return 0;
        }

        if (message == TrayCallbackMessage)
        {
            // After NIM_SETVERSION/NOTIFYICON_VERSION_4 the notification code occupies LOWORD(lParam)
            // and the icon id occupies HIWORD(lParam). Comparing the full lParam would make every
            // callback look different once version 4 is active and silently break tray interactions.
            var mouseMessage = unchecked((uint)lParam.ToInt64()) & 0xFFFFu;
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

    private bool TryAddTrayIcon()
    {
        if (_windowHandle == 0 || _disposed)
        {
            return false;
        }

        Icon? desiredIcon = null;
        StatusIconKey? desiredKey = null;
        string desiredTip;
        lock (_statusIconSync)
        {
            desiredTip = _currentTip;
            if (_desiredIconKey is StatusIconKey key && _statusIconCache.TryGetValue(key, out var cached))
            {
                desiredKey = key;
                desiredIcon = cached;
            }
        }

        var data = CreateNotifyIconData(NifMessage | NifIcon | NifTip | NifShowTip);
        data.CallbackMessage = TrayCallbackMessage;
        data.Icon = desiredIcon?.Handle ?? _sharedFallbackIconHandle;
        data.Tip = desiredTip;
        if (!Shell_NotifyIconW(NimAdd, ref data))
        {
            return false;
        }

        lock (_statusIconSync)
        {
            _lastAppliedTip = desiredTip;
            _currentIconKey = desiredKey;
        }

        var version = CreateNotifyIconData(0);
        version.TimeoutOrVersion = NotifyIconVersion4;
        _ = Shell_NotifyIconW(NimSetVersion, ref version);
        return true;
    }

    private void QueueStatusIconRender(StatusIconKey key)
    {
        _ = Task.Run(() =>
        {
            Icon? created = null;
            try
            {
                // System.Drawing font family discovery/icon rasterization was a measured dispatcher
                // hotspot. Cache misses are intentionally rendered on the thread pool.
                created = BuildStatusIcon(key.RemainingPercent, key.IsFresh);

                nint window;
                lock (_statusIconSync)
                {
                    _pendingIconKeys.Remove(key);
                    if (_disposed)
                    {
                        window = 0;
                    }
                    else if (_statusIconCache.ContainsKey(key))
                    {
                        window = _windowHandle;
                    }
                    else
                    {
                        _statusIconCache.Add(key, created);
                        created = null; // cache owns the icon until service disposal
                        window = _windowHandle;
                    }
                }

                if (window != 0)
                {
                    _ = PostMessageW(window, ApplyStatusMessage, 0, 0);
                }
            }
            catch
            {
                lock (_statusIconSync)
                {
                    _pendingIconKeys.Remove(key);
                }
                // A cosmetic badge render must never terminate the background utility. The fallback
                // shell icon remains registered and a later state can retry with another key.
            }
            finally
            {
                created?.Dispose();
            }
        });
    }

    private void ApplyDesiredStatus()
    {
        StatusIconKey key;
        string tip;
        Icon icon;
        lock (_statusIconSync)
        {
            if (_disposed || _windowHandle == 0 ||
                _desiredIconKey is not StatusIconKey desiredKey ||
                !_statusIconCache.TryGetValue(desiredKey, out var cached))
            {
                return;
            }

            key = desiredKey;
            tip = _currentTip;
            icon = cached;
            if (_currentIconKey == key && string.Equals(_lastAppliedTip, tip, StringComparison.Ordinal))
            {
                return;
            }
        }

        ApplyStatusIcon(key, tip, icon);
    }

    private void ApplyStatusIcon(StatusIconKey key, string tip, Icon icon)
    {
        if (_windowHandle == 0 || _disposed)
        {
            return;
        }

        var data = CreateNotifyIconData(NifTip | NifIcon | NifShowTip);
        data.Tip = tip;
        data.Icon = icon.Handle;
        if (!Shell_NotifyIconW(NimModify, ref data))
        {
            return;
        }

        lock (_statusIconSync)
        {
            if (_disposed)
            {
                return;
            }

            _lastAppliedTip = tip;
            _currentIconKey = key;
        }
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
            _ = PostMessageW(_windowHandle, WmNull, 0, 0);

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
        Flags = flags,
        Tip = string.Empty,
        Info = string.Empty,
        InfoTitle = string.Empty
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

        _sharedFallbackIconHandle = 0; // LoadIcon returns a shared system icon; it must not be destroyed.
    }

    private static Icon BuildStatusIcon(int? remainingPercent, bool isFresh)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(isFresh ? Color.FromArgb(104, 58, 183) : Color.FromArgb(90, 90, 90));

        var label = remainingPercent is int percent
            ? Math.Clamp(percent, 0, 100).ToString(percent >= 100 ? "000" : "00")
            : "?";
        using var font = new Font("Segoe UI", label.Length >= 3 ? 8f : 10f, FontStyle.Bold, GraphicsUnit.Pixel);
        var size = graphics.MeasureString(label, font);
        using var brush = new SolidBrush(Color.White);
        graphics.DrawString(label, font, brush, (32f - size.Width) / 2f, (32f - size.Height) / 2f);

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 1)] + "…";

    private readonly record struct StatusIconKey(int? RemainingPercent, bool IsFresh);

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

    private const uint WmNull = 0x0000;
    private const uint WmApp = 0x8000;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NotifyIconVersion4 = 4;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NifShowTip = 0x00000080;
    private const uint NiifInfo = 0x00000001;
    private const uint MfString = 0x00000000;
    private const uint MfChecked = 0x00000008;
    private const uint MfUnchecked = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNoNotify = 0x0080;
    private const uint TpmReturnCmd = 0x0100;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

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
