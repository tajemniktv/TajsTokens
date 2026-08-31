using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ISystemTrayService : IDisposable
{
    event EventHandler? OpenDashboardRequested;
    event EventHandler? RefreshRequested;
    event EventHandler? ExitRequested;
    event Action<bool>? NotificationsEnabledChanged;
    event Action<bool>? LaunchAtLoginChanged;

    void Initialize();
    void UpdateStatus(SystemTrayStatus status);
    void UpdatePreferences(bool notificationsEnabled, bool launchAtLogin);
    void ShowNotification(string title, string message);
}
