// Taj's Tokens | App.xaml.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using TajsTokens.App.Services;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;

#endregion

namespace TajsTokens.App;

public partial class App : Application
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _settingsApplyGate = new(1, 1);
    private readonly WindowsStartupRegistrationService _startupService = new();
    private readonly WindowsSystemTrayService _trayService = new();
    private DispatcherQueue? _dispatcher;
    private DogfoodLifetime? _dogfoodLifetime;
    private bool _exitRequested;
    private CancellationTokenSource? _periodicCancellation;
    private IDisposable? _startupLease;
    private Window? _window;

    public App()
    {
        // The deployment-owned child is started while its parent holds this same lease.
        if (!Environment.GetCommandLineArgs().Contains("--dogfood-start", StringComparer.Ordinal))
        {
            _startupLease = AppStartupLease.TryAcquire(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (_startupLease is null) Environment.Exit(0);
        }
        InitializeComponent();
        Services = new AppServices();
    }

    public AppServices Services { get; }

    public void Navigate(Type pageType, object? parameter = null)
    {
        (_window as MainWindow)?.Navigate(pageType, parameter);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _dogfoodLifetime = new DogfoodLifetime(() => RunOnDispatcher(RequestExit));

        _trayService.OpenDashboardRequested += (_, _) => ShowDashboard();
        _trayService.RefreshRequested += (_, _) => _ = RefreshFromTrayAsync();
        _trayService.ExitRequested += (_, _) => RequestExit();
        _trayService.NotificationsEnabledChanged += OnNotificationsEnabledChanged;
        _trayService.LaunchAtLoginChanged += OnLaunchAtLoginChanged;
        _trayService.Initialize();
        _trayService.UpdatePreferences(Services.Settings.NotificationsEnabled, Services.Settings.LaunchAtLogin);
        _ = ReconcileStartupRegistrationAsync();

        Services.Telemetry.SnapshotUpdated += OnSnapshotUpdated;
        Services.SettingsChanged += OnSettingsChanged;

        bool startHidden = args.Arguments
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        if (!startHidden)
        {
            ShowDashboard();
        }

        StartPeriodicCollector();
        _dogfoodLifetime.MarkReady();
        _startupLease?.Dispose();
        _startupLease = null;
    }

    private async Task ReconcileStartupRegistrationAsync()
    {
        try
        {
            (bool Success, string? Error, bool Enabled) result;
            await _settingsApplyGate.WaitAsync();
            try
            {
                // Read the desired value only after entering the same gate used by interactive
                // settings changes. A slow launch-time registry write therefore cannot apply an old
                // preference after a newer user choice.
                bool enabled = Services.Settings.LaunchAtLogin;
                (bool Success, string? Error) applied = await Task.Run(() =>
                {
                    try
                    {
                        bool success = _startupService.TrySetEnabled(enabled, out string? error);
                        return (Success: success, Error: error);
                    }
                    catch (Exception exception)
                    {
                        return (Success: false, Error: exception.Message.ReplaceLineEndings(" "));
                    }
                });
                result = (applied.Success, applied.Error, enabled);
            }
            finally
            {
                _settingsApplyGate.Release();
            }

            if (!result.Success && result.Enabled)
            {
                RunOnDispatcher(() =>
                    _trayService.ShowNotification(
                        "TajsTokens startup registration failed",
                        result.Error ?? "Unknown startup registration error."));
            }
        }
        catch (Exception exception)
        {
            // This operation is intentionally detached from launch. Observe every failure here so a
            // registry or runtime surprise cannot become an unobserved fire-and-forget exception.
            RunOnDispatcher(() =>
                _trayService.ShowNotification(
                    "TajsTokens startup registration failed",
                    exception.Message.ReplaceLineEndings(" ")));
        }
    }

    private void StartPeriodicCollector()
    {
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _periodicCancellation,
            CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token));
        previous?.Cancel();
        previous?.Dispose();

        CancellationToken token = _periodicCancellation.Token;
        TimeSpan interval = TimeSpan.FromSeconds(Services.Settings.PollIntervalSeconds);

        // Microsoft.Data.Sqlite performs its SQLite work synchronously even behind many async APIs.
        // Starting the collector directly from OnLaunched therefore lets its continuations inherit
        // WinUI's DispatcherQueueSynchronizationContext and can freeze the window during a first-run
        // rollout scan. Keep the entire process-lifetime collector on the thread pool instead.
        _ = Task.Run(
            () => Services.Telemetry.RunPeriodicAsync(interval, token),
            token);
    }

    private void OnSettingsChanged(RuntimeSettings previous, RuntimeSettings current)
    {
        if (previous.PollIntervalSeconds != current.PollIntervalSeconds && !_exitRequested)
        {
            StartPeriodicCollector();
        }
    }

    private void OnSnapshotUpdated(TelemetrySnapshot snapshot)
    {
        _dispatcher?.TryEnqueue(() =>
        {
            CodexIntelligenceSnapshot intelligence = Services.CodexIntelligence.Current;
            _trayService.UpdateStatus(SystemTrayStatusPresenter.Build(intelligence));

            // Advance alert state even while notifications are muted. Otherwise re-enabling them can
            // replay stale threshold/provider transitions that happened while the user opted out.
            IReadOnlyList<AlertNotification> alerts = Services.AlertEngine.Evaluate(intelligence);
            if (!Services.Settings.NotificationsEnabled)
            {
                return;
            }

            foreach (AlertNotification alert in alerts)
            {
                _trayService.ShowNotification(alert.Title, alert.Message);
            }
        });
    }

    private async Task RefreshFromTrayAsync()
    {
        try
        {
            await Services.Telemetry.RefreshAsync(RefreshTrigger.Manual, _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }

    private async void OnNotificationsEnabledChanged(bool enabled)
    {
        if (!await TrySaveSettingsUpdateAsync(current => current with { NotificationsEnabled = enabled }))
        {
            UpdateTrayPreferences(Services.Settings);
        }
    }

    private async void OnLaunchAtLoginChanged(bool enabled)
    {
        if (!await TrySaveSettingsUpdateAsync(current => current with { LaunchAtLogin = enabled }))
        {
            UpdateTrayPreferences(Services.Settings);
        }
    }

    /// <summary>
    ///     Applies the complete Settings-page form only if it is still based on the current runtime
    ///     settings generation. If another surface changed settings since the page rendered, the save is
    ///     rejected instead of silently reverting that newer change.
    /// </summary>
    public Task<(bool Success, string? Error)> TryApplySettingsAsync(
        RuntimeSettings expectedBase,
        RuntimeSettings settings)
    {
        return ApplySettingsAsync(_ => settings, expectedBase);
    }

    private Task<(bool Success, string? Error)> TryUpdateSettingsAsync(
        Func<RuntimeSettings, RuntimeSettings> update)
    {
        return ApplySettingsAsync(update, null);
    }

    private async Task<(bool Success, string? Error)> ApplySettingsAsync(
        Func<RuntimeSettings, RuntimeSettings> update,
        RuntimeSettings? expectedBase)
    {
        await _settingsApplyGate.WaitAsync();
        try
        {
            RuntimeSettings previous = Services.Settings;
            if (expectedBase is not null && !ReferenceEquals(previous, expectedBase))
            {
                return (false,
                    "Settings changed from another surface while this page was open. Reload the current values and apply your changes again.");
            }

            // Build field-specific tray updates only after entering the gate. That prevents a queued
            // toggle from carrying a stale full settings snapshot that reverts unrelated fields.
            RuntimeSettings settings = update(previous);
            (bool Success, string? Error) result = await Task.Run(() => ApplySettingsCore(previous, settings));
            UpdateTrayPreferences(result.Success ? Services.Settings : previous);
            return result;
        }
        finally
        {
            _settingsApplyGate.Release();
        }
    }

    private (bool Success, string? Error) ApplySettingsCore(RuntimeSettings previous, RuntimeSettings settings)
    {
        bool startupChanged = previous.LaunchAtLogin != settings.LaunchAtLogin;

        try
        {
            if (startupChanged && !_startupService.TrySetEnabled(settings.LaunchAtLogin, out string? startupError))
            {
                return (false, startupError ?? "Start-with-Windows registration could not be updated.");
            }

            Services.SaveSettings(settings);
            return (true, null);
        }
        catch (Exception exception)
        {
            if (startupChanged)
            {
                try
                {
                    _ = _startupService.TrySetEnabled(previous.LaunchAtLogin, out _);
                }
                catch
                {
                    // The original settings remain authoritative even if best-effort registry
                    // rollback itself fails. The caller receives the primary apply error.
                }
            }

            return (false, exception.Message.ReplaceLineEndings(" "));
        }
    }

    private async Task<bool> TrySaveSettingsUpdateAsync(Func<RuntimeSettings, RuntimeSettings> update)
    {
        (bool Success, string? Error) result = await TryUpdateSettingsAsync(update);
        if (result.Success)
        {
            return true;
        }

        RunOnDispatcher(() =>
            _trayService.ShowNotification(
                "TajsTokens settings could not be applied",
                result.Error ?? "Unknown settings error."));
        return false;
    }

    private void UpdateTrayPreferences(RuntimeSettings settings)
    {
        RunOnDispatcher(() =>
            _trayService.UpdatePreferences(settings.NotificationsEnabled, settings.LaunchAtLogin));
    }

    private void RunOnDispatcher(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested || !Services.Settings.RunInBackground)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
    }

    private void ShowDashboard()
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Title = "TajsTokens";
            _window.AppWindow.Closing += OnWindowClosing;
        }

        _window.AppWindow.Show();
        _window.Activate();
    }

    private void RequestExit()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        _lifetimeCancellation.Cancel();
        _periodicCancellation?.Cancel();
        _periodicCancellation?.Dispose();
        _periodicCancellation = null;
        Services.Telemetry.SnapshotUpdated -= OnSnapshotUpdated;
        Services.SettingsChanged -= OnSettingsChanged;
        _trayService.Dispose();
        _dogfoodLifetime?.Dispose();
        _window?.Close();
        Exit();
    }
}