using TajsTokens.Core.Interfaces;

namespace TajsTokens.App.Services;

public sealed class NoOpSystemTrayService : ISystemTrayService
{
    public void Initialize()
    {
        // Intentionally no-op for now. A dedicated Win32 tray integration service will be added
        // once cross-version behavior and app lifetime handling are fully validated.
    }

    public void ShowNotification(string title, string message)
    {
        _ = title;
        _ = message;
    }
}
