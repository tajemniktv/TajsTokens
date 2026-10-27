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
    private readonly WindowsResourcePolicy _resourcePolicy = new();
    private DispatcherQueue? _dispatcher;
    private DogfoodLifetime? _dogfoodLifetime;
    private bool _exitRequested;
    private CancellationTokenSource? _periodicCancellation;
    private IDisposable? _startupLease;
    private Window? _window;
    private bool _dashboardOpenQueued;

    public App()
    {
        if (Environment.GetCommandLineArgs().Contains("--open-dashboard", StringComparer.OrdinalIgnoreCase) &&
            DogfoodLifetime.TryRequestDashboard()) Environment.Exit(0);
        // The deployment-owned child is started while its parent holds this same lease.
        if (!Environment.GetCommandLineArgs().Contains("--dogfood-start", StringComparer.Ordinal))
        {
            _startupLease = AppStartupLease.TryAcquire(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (_startupLease is null) Environment.Exit(0);
        }
        InitializeComponent();
        Services = new AppServices(() => _resourcePolicy.IsBackground);
        _resourcePolicy.IsBusy = () => Services.Telemetry.IsForecastRefreshRunning;
    }

    public AppServices Services { get; }

    public void Navigate(Type pageType, object? parameter = null)
    {
        (_window as MainWindow)?.Navigate(pageType, parameter);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _dogfoodLifetime = new DogfoodLifetime(() => RunOnDispatcher(RequestExit), () => RunOnDispatcher(RequestDashboard));

        _trayService.OpenDashboardRequested += (_, _) => RequestDashboard();
        _trayService.RefreshRequested += (_, _) => _ = RefreshFromTrayAsync();
        _trayService.ExitRequested += (_, _) => RequestExit();
        _trayService.NotificationsEnabledChanged += OnNotificationsEnabledChanged;
        _trayService.LaunchAtLoginChanged += OnLaunchAtLoginChanged;
        _trayService.Initialize();
        _trayService.UpdatePreferences(Services.Settings.NotificationsEnabled, Services.Settings.LaunchAtLogin);
        _ = ReconcileStartupRegistrationAsync();

        Services.Telemetry.SnapshotUpdated += OnSnapshotUpdated;
        Services.SettingsChanged += OnSettingsChanged;
        Services.LocalLimitsApi.Configure(Services.Settings.LocalLimitsApiEnabled,
            Services.Settings.LocalLimitsApiPort, Services.Settings.LocalLimitsApiToken);

        // Unpackaged WinUI desktop activation can leave args.Arguments empty. The login command's
        // --background switch must also be read from the actual process command line.
        bool startHidden = Environment.GetCommandLineArgs().Skip(1)
            .Concat(args.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        if (!startHidden)
        {
            ShowDashboard();
        }
        else _resourcePolicy.SetDashboardVisible(false);

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
        if (previous.LocalLimitsApiEnabled != current.LocalLimitsApiEnabled ||
            previous.LocalLimitsApiPort != current.LocalLimitsApiPort ||
            previous.LocalLimitsApiToken != current.LocalLimitsApiToken)
            Services.LocalLimitsApi.Configure(current.LocalLimitsApiEnabled,
                current.LocalLimitsApiPort, current.LocalLimitsApiToken);
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
            if (snapshot.Events.Any(item => item.Type == "Refresh completed"))
                _resourcePolicy.ScheduleReclamation();

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
        // Progressive quota/scan snapshots do not each need a separate fleet database query.
        if (snapshot.Events.Any(item => item.Type == "Refresh completed"))
            _ = UpdateRecentFleetTrayAsync(snapshot);
    }

    private async Task UpdateRecentFleetTrayAsync(TelemetrySnapshot snapshot)
    {
        try
        {
            CodexLiveFleetSnapshot fleet = await Services.CodexThreadReadModel.ReadLiveFleetAsync(_lifetimeCancellation.Token);
            if (!ReferenceEquals(Services.Telemetry.Latest, snapshot) || _exitRequested) return;
            int recent = fleet.Threads.Count(thread => thread.State == CodexFleetState.RecentlyWorking);
            SystemTrayStatus status = SystemTrayStatusPresenter.WithRecentFleet(
                SystemTrayStatusPresenter.Build(Services.CodexIntelligence.Current), recent);
            _dispatcher?.TryEnqueue(() =>
            {
                if (!_exitRequested && ReferenceEquals(Services.Telemetry.Latest, snapshot))
                    _trayService.UpdateStatus(status);
            });
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            // Quota status remains available when Codex thread history is absent or unreadable.
        }
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
        UpdateDashboardVisibility();
    }

    private void RequestDashboard()
    {
        // Lift the process cap before any XAML construction, page reload, or queued activation.
        _resourcePolicy.SetDashboardVisible(true);
        Services.Calibration.RefreshBudget();
        if (_dashboardOpenQueued || _exitRequested) return;
        _dashboardOpenQueued = true;
        _dispatcher?.TryEnqueue(DispatcherQueuePriority.High, () =>
        {
            try { ShowDashboard(); }
            finally { _dashboardOpenQueued = false; }
        });
    }

    private void ShowDashboard()
    {
        _resourcePolicy.SetDashboardVisible(true);
        Services.Calibration.RefreshBudget();
        bool wasVisible = _window?.AppWindow.IsVisible == true &&
            _window.AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Title = "TajsTokens";
            _window.AppWindow.Closing += OnWindowClosing;
            _window.AppWindow.Changed += (_, _) => UpdateDashboardVisibility();
            // Presenter state can settle after the native activation/size notification.
            _window.Activated += (_, _) => _dispatcher?.TryEnqueue(UpdateDashboardVisibility);
            _window.SizeChanged += (_, _) => _dispatcher?.TryEnqueue(UpdateDashboardVisibility);
        }

        _window.AppWindow.Show();
        if (_window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } minimized)
            minimized.Restore();
        _window.Activate();
        UpdateDashboardVisibility();
        if (!wasVisible && Services.Telemetry.Latest.HasAnyData) _ = RefreshFromTrayAsync();
    }

    private void UpdateDashboardVisibility()
    {
        if (_exitRequested || _window is not MainWindow window) return;
        bool visible = window.AppWindow.IsVisible &&
            (window.AppWindow.Presenter is not OverlappedPresenter presenter ||
             presenter.State != OverlappedPresenterState.Minimized);
        bool enteringBackground = !visible && !_resourcePolicy.IsBackground;
        _resourcePolicy.SetDashboardVisible(visible);
        Services.Calibration.RefreshBudget();
        window.SetDashboardVisible(visible);
        if (enteringBackground) Services.Telemetry.SuspendDetailedForecasts();
    }

    private void RequestExit()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        // Shutdown and cancellation must not compete for the tray's tiny CPU budget.
        _resourcePolicy.SetDashboardVisible(true);
        _lifetimeCancellation.Cancel();
        _periodicCancellation?.Cancel();
        _periodicCancellation?.Dispose();
        _periodicCancellation = null;
        Services.Telemetry.SnapshotUpdated -= OnSnapshotUpdated;
        Services.SettingsChanged -= OnSettingsChanged;
        Services.LocalLimitsApi.Dispose();
        Services.Calibration.Dispose();
        _resourcePolicy.Dispose();
        _trayService.Dispose();
        _dogfoodLifetime?.Dispose();
        _window?.Close();
        Exit();
    }
}
