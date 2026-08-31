using System.Drawing;
using System.Runtime.InteropServices;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using Forms = System.Windows.Forms;

namespace TajsTokens.App.Services;

/// <summary>
/// Lightweight notification-area surface for Phase 2. It intentionally exposes only the constrained
/// quota, health and quick lifecycle actions; detailed diagnostics remain in the WinUI dashboard.
/// </summary>
public sealed class WindowsSystemTrayService : ISystemTrayService
{
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _notificationsItem;
    private Forms.ToolStripMenuItem? _launchAtLoginItem;
    private Icon? _statusIcon;
    private bool _disposed;

    public event EventHandler? OpenDashboardRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? ExitRequested;
    public event Action<bool>? NotificationsEnabledChanged;
    public event Action<bool>? LaunchAtLoginChanged;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_notifyIcon is not null)
        {
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Open TajsTokens");
        openItem.Click += (_, _) => OpenDashboardRequested?.Invoke(this, EventArgs.Empty);

        var refreshItem = new Forms.ToolStripMenuItem("Refresh telemetry");
        refreshItem.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

        _notificationsItem = new Forms.ToolStripMenuItem("Notifications enabled")
        {
            CheckOnClick = true
        };
        _notificationsItem.Click += (_, _) => NotificationsEnabledChanged?.Invoke(_notificationsItem.Checked);

        _launchAtLoginItem = new Forms.ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true
        };
        _launchAtLoginItem.Click += (_, _) => LaunchAtLoginChanged?.Invoke(_launchAtLoginItem.Checked);

        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(openItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_notificationsItem);
        menu.Items.Add(_launchAtLoginItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _statusIcon = BuildStatusIcon(null, isFresh: false);
        _notifyIcon = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "TajsTokens · waiting for telemetry",
            Icon = _statusIcon,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenDashboardRequested?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateStatus(SystemTrayStatus status)
    {
        if (_notifyIcon is null || _disposed)
        {
            return;
        }

        _notifyIcon.Text = Truncate(status.Tooltip, 63);
        var replacement = BuildStatusIcon(status.ConstrainedRemainingPercent, status.IsFresh);
        _notifyIcon.Icon = replacement;
        _statusIcon?.Dispose();
        _statusIcon = replacement;
    }

    public void UpdatePreferences(bool notificationsEnabled, bool launchAtLogin)
    {
        if (_disposed)
        {
            return;
        }

        if (_notificationsItem is not null)
        {
            _notificationsItem.Checked = notificationsEnabled;
        }

        if (_launchAtLoginItem is not null)
        {
            _launchAtLoginItem.Checked = launchAtLogin;
        }
    }

    public void ShowNotification(string title, string message)
    {
        if (_notifyIcon is null || _disposed)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = Truncate(title, 63);
        _notifyIcon.BalloonTipText = Truncate(message, 255);
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(6000);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _notificationsItem = null;
        _launchAtLoginItem = null;
        _statusIcon?.Dispose();
        _statusIcon = null;
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
        graphics.DrawString(label, font, brush, (32 - size.Width) / 2f, (32 - size.Height) / 2f);

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 1)] + "…";

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);
}
