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

        if (!_startupService.TrySetEnabled(Services.Settings.LaunchAtLogin, out var startupError) &&
            Services.Settings.LaunchAtLogin)
        {
            _trayService.ShowNotification("TajsTokens startup registration failed", startupError ?? "Unknown startup registration error.");
        }

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

    private void StartPeriodicCollector()
    {
        var previous = Interlocked.Exchange(
            ref _periodicCancellation,
            CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token));
        previous?.Cancel();
        previous?.Dispose();

        var token = _periodicCancellation.Token;
        _ = Services.Telemetry.RunPeriodicAsync(
            TimeSpan.FromSeconds(Services.Settings.PollIntervalSeconds),
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

    private void OnNotificationsEnabledChanged(bool enabled)
    {
        if (!TrySaveSettings(Services.Settings with { NotificationsEnabled = enabled }))
        {
            _trayService.UpdatePreferences(Services.Settings.NotificationsEnabled, Services.Settings.LaunchAtLogin);
        }
    }

    private void OnLaunchAtLoginChanged(bool enabled)
    {
        if (!_startupService.TrySetEnabled(enabled, out var error))
        {
            _trayService.UpdatePreferences(Services.Settings.NotificationsEnabled, Services.Settings.LaunchAtLogin);
            _trayService.ShowNotification("Could not update Start with Windows", error ?? "Unknown startup registration error.");
            return;
        }

        if (!TrySaveSettings(Services.Settings with { LaunchAtLogin = enabled }))
        {
            _ = _startupService.TrySetEnabled(Services.Settings.LaunchAtLogin, out _);
            _trayService.UpdatePreferences(Services.Settings.NotificationsEnabled, Services.Settings.LaunchAtLogin);
        }
    }

    private bool TrySaveSettings(RuntimeSettings settings)
    {
        try
        {
            Services.SaveSettings(settings);
            _trayService.UpdatePreferences(Services.Settings.NotificationsEnabled, Services.Settings.LaunchAtLogin);
            return true;
        }
        catch (Exception exception)
        {
            _trayService.ShowNotification("TajsTokens settings could not be saved", exception.Message.ReplaceLineEndings(" "));
            return false;
        }
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
