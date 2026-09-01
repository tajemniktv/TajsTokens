using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using TajsTokens.App.Services;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.App;

public partial class App : Application
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly WindowsSystemTrayService _trayService = new();
    private readonly WindowsStartupRegistrationService _startupService = new();
    private readonly SemaphoreSlim _settingsApplyGate = new(1, 1);
    private CancellationTokenSource? _periodicCancellation;
    private Window? _window;
    private DispatcherQueue? _dispatcher;
    private bool _exitRequested;

    public App()
    {
        InitializeComponent();
        Services = new AppServices();
    }

    public AppServices Services { get; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();

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

        var startHidden = args.Arguments
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        if (!startHidden)
        {
            ShowDashboard();
        }

        StartPeriodicCollector();
    }

    private async Task ReconcileStartupRegistrationAsync()
    {
        var enabled = Services.Settings.LaunchAtLogin;
        var result = await Task.Run(() =>
        {
            var success = _startupService.TrySetEnabled(enabled, out var error);
            return (Success: success, Error: error);
        });

        if (!result.Success && enabled)
        {
            RunOnDispatcher(() =>
                _trayService.ShowNotification(
                    "TajsTokens startup registration failed",
                    result.Error ?? "Unknown startup registration error."));
        }
    }

    private void StartPeriodicCollector()
    {
        var previous = Interlocked.Exchange(
            ref _periodicCancellation,
            CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token));
        previous?.Cancel();
        previous?.Dispose();

        var token = _periodicCancellation.Token;
        var interval = TimeSpan.FromSeconds(Services.Settings.PollIntervalSeconds);

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
            _trayService.UpdateStatus(BuildTrayStatus(snapshot));

            // Advance alert state even while notifications are muted. Otherwise re-enabling them can
            // replay stale threshold/provider transitions that happened while the user opted out.
            var alerts = Services.AlertEngine.Evaluate(snapshot);
            if (!Services.Settings.NotificationsEnabled)
            {
                return;
            }

            foreach (var alert in alerts)
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
        if (!await TrySaveSettingsAsync(Services.Settings with { NotificationsEnabled = enabled }))
        {
            UpdateTrayPreferences(Services.Settings);
        }
    }

    private async void OnLaunchAtLoginChanged(bool enabled)
    {
        if (!await TrySaveSettingsAsync(Services.Settings with { LaunchAtLogin = enabled }))
        {
            UpdateTrayPreferences(Services.Settings);
        }
    }

    /// <summary>
    /// Applies user-facing runtime settings through one application-owned boundary so persisted
    /// values, Start-with-Windows registration, tray preferences and live scheduler updates cannot
    /// drift depending on which UI surface changed a setting. Registry and settings-file I/O run on
    /// the thread pool; only the resulting tray/UI state is marshalled back to WinUI.
    /// </summary>
    public async Task<(bool Success, string? Error)> TryApplySettingsAsync(RuntimeSettings settings)
    {
        await _settingsApplyGate.WaitAsync();
        try
        {
            var previous = Services.Settings;
            var result = await Task.Run(() => ApplySettingsCore(previous, settings));
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
        var startupChanged = previous.LaunchAtLogin != settings.LaunchAtLogin;

        if (startupChanged && !_startupService.TrySetEnabled(settings.LaunchAtLogin, out var startupError))
        {
            return (false, startupError ?? "Start-with-Windows registration could not be updated.");
        }

        try
        {
            Services.SaveSettings(settings);
            return (true, null);
        }
        catch (Exception exception)
        {
            if (startupChanged)
            {
                _ = _startupService.TrySetEnabled(previous.LaunchAtLogin, out _);
            }

            return (false, exception.Message.ReplaceLineEndings(" "));
        }
    }

    private async Task<bool> TrySaveSettingsAsync(RuntimeSettings settings)
    {
        var result = await TryApplySettingsAsync(settings);
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

    private void UpdateTrayPreferences(RuntimeSettings settings) =>
        RunOnDispatcher(() =>
            _trayService.UpdatePreferences(settings.NotificationsEnabled, settings.LaunchAtLogin));

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
        _window?.Close();
        Exit();
    }

    private static SystemTrayStatus BuildTrayStatus(TelemetrySnapshot snapshot)
    {
        var fiveHour = LatestQuota(snapshot, QuotaWindowKind.FiveHour);
        var weekly = LatestQuota(snapshot, QuotaWindowKind.Weekly);
        var remainingValues = new[] { fiveHour?.RemainingPercent, weekly?.RemainingPercent }
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();

        int? constrained = remainingValues.Length == 0
            ? null
            : (int)Math.Round(remainingValues.Min(), MidpointRounding.AwayFromZero);

        var health = snapshot.QuotaDataFresh
            ? "live"
            : snapshot.QuotaSnapshots.Count > 0
                ? "stale"
                : "quota unavailable";

        var tooltip = $"TajsTokens · 5h {FormatQuota(fiveHour)} · week {FormatQuota(weekly)} · {health}";
        return new SystemTrayStatus(tooltip, constrained, snapshot.QuotaDataFresh, health);
    }

    private static QuotaSnapshot? LatestQuota(TelemetrySnapshot snapshot, QuotaWindowKind kind) =>
        snapshot.QuotaSnapshots
            .Where(item => item.Kind == kind)
            .OrderByDescending(item => item.CapturedAtUtc)
            .FirstOrDefault();

    private static string FormatQuota(QuotaSnapshot? snapshot) =>
        snapshot?.RemainingPercent is double remaining ? $"{remaining:0}%" : "?";
}
