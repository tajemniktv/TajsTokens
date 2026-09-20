// Taj's Tokens | ISystemTrayService.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

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