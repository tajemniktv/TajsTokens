using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ISystemTrayService : IDisposable
{
    event EventHandler? OpenDashboardRequested;
    event EventHandler? RefreshRequested;
    event EventHandler? ExitRequested;

    void Initialize();
    void UpdateStatus(SystemTrayStatus status);
    void ShowNotification(string title, string message);
}
