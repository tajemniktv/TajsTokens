using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.App.Pages;

public sealed partial class ForecastsPage : Page
{
    private CancellationTokenSource? _pageCancellation;
    private bool _isLoaded;
    private long _loadGeneration;
    private long _estimateGeneration;

    public ForecastsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private App App => (App)Application.Current;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        var previous = Interlocked.Exchange(ref _pageCancellation, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
        var generation = Interlocked.Increment(ref _loadGeneration);
        await LoadAsync(_pageCancellation.Token, generation);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        Interlocked.Increment(ref _loadGeneration);
        Interlocked.Increment(ref _estimateGeneration);
        var cancellation = Interlocked.Exchange(ref _pageCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        var cancellation = _pageCancellation;
        if (!_isLoaded || cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _loadGeneration);
        await LoadAsync(cancellation.Token, generation);
    }

    private async Task LoadAsync(CancellationToken cancellationToken, long generation)
    {
        if (!_isLoaded)
        {
            return;
        }

        try
        {
            StatusText.Text = "Loading persisted forecast history…";
            var now = DateTimeOffset.UtcNow;
            var days = ParseHistoryDays();
            var dashboard = await Task.Run(
                () => App.Services.Intelligence.QueryAsync(
                    new IntelligenceQuery(now.AddDays(-days), now, AnalyticsBucketSize.Day, 720),
                    cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (!_isLoaded || generation != Volatile.Read(ref _loadGeneration))
            {
                return;
            }
            RenderForecasts(dashboard);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_isLoaded && generation == Volatile.Read(ref _loadGeneration))
            {
                StatusText.Text = $"Forecast history unavailable: {Summarize(exception.Message)}";
            }
        }
    }

    private void RenderForecasts(IntelligenceDashboard dashboard)
    {
        FiveHourSamplesText.Text = dashboard.FiveHourForecasts.Count.ToString("N0");
        WeeklySamplesText.Text = dashboard.WeeklyForecasts.Count.ToString("N0");
        FiveHourSafeText.Text = FormatSurvivalRate(dashboard.FiveHourForecasts);
        WeeklySafeText.Text = FormatSurvivalRate(dashboard.WeeklyForecasts);

        var rows = dashboard.FiveHourForecasts
            .Concat(dashboard.WeeklyForecasts)
            .OrderByDescending(snapshot => snapshot.Forecast.GeneratedAtUtc)
            .Take(400)
            .Select(snapshot =>
            {
                var forecast = snapshot.Forecast;
                var outcome = forecast.EstimatedExhaustionAtUtc is DateTimeOffset exhaustion
                    ? $"exhaustion {exhaustion.ToLocalTime():g}"
                    : forecast.ProjectedRemainingAtResetPercent is double margin
                        ? $"~{margin:0.#}% at reset"
                        : "outcome learning";
                var pace = forecast.BurnRatePercentPerHour is double burn
                    ? $"{burn:0.00} pp/h"
                    : "pace uncertain";
                var pressure = forecast.BurnPressure is double value ? $" · {value:0.00}× sustainable" : string.Empty;
                var anchor = snapshot.QuotaCapturedAtUtc is DateTimeOffset captured
                    ? $" · anchor {snapshot.QuotaAuthority} @ {captured.ToLocalTime():g} · {snapshot.QuotaSource ?? "unknown source"}"
                    : " · legacy forecast without quota-anchor metadata";
                return new ForecastRow(
                    $"{forecast.GeneratedAtUtc.ToLocalTime():g} · {FormatKind(forecast.Kind)} · {forecast.State}",
                    $"{pace}{pressure} · {outcome} · confidence {forecast.Confidence:P0}" +
                    (string.IsNullOrWhiteSpace(forecast.Trend) ? string.Empty : $" · {forecast.Trend}") + anchor);
            })
            .ToArray();

        ForecastList.ItemsSource = rows.Length == 0
            ? new[] { new ForecastRow("No forecast history yet", "Phase 4 now persists reset-aware forecasts during shared telemetry refreshes. Let the collector observe a few quota samples first.") }
            : rows;

        StatusText.Text =
            $"{dashboard.FiveHourForecasts.Count + dashboard.WeeklyForecasts.Count:N0} persisted forecast sample(s) in the selected range. " +
            "New samples preserve the provider-authoritative quota anchor used by Overview; legacy rows remain explicitly unattributed.";
    }

    private async void OnEstimateClicked(object sender, RoutedEventArgs e)
    {
        var cancellation = _pageCancellation;
        if (!_isLoaded || cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _estimateGeneration);
        try
        {
            var request = new ScenarioRequest(
                ReadNumber(DurationBox, 2),
                (int)Math.Round(ReadNumber(RootAgentsBox, 1)),
                (int)Math.Round(ReadNumber(SubagentsBox, 0)),
                ReadNumber(IntensityBox, 1),
                NullIfWhiteSpace(ModelBox.Text),
                NullIfWhiteSpace(ReasoningBox.Text));
            var historyFrom = DateTimeOffset.UtcNow.AddDays(-ParseHistoryDays());
            ScenarioInfo.IsOpen = true;
            ScenarioInfo.Severity = InfoBarSeverity.Informational;
            ScenarioInfo.Title = "Estimating…";
            ScenarioInfo.Message = "Fitting account-local history for both quota windows.";
            ScenarioMethodText.Text = string.Empty;

            var estimate = await Task.Run(
                () => App.Services.Intelligence.EstimateScenarioAsync(request, historyFrom, cancellation.Token),
                cancellation.Token);
            if (!_isLoaded || cancellation.IsCancellationRequested || generation != Volatile.Read(ref _estimateGeneration))
            {
                return;
            }

            var bothReady = estimate.FiveHour.HasEnoughHistory && estimate.Weekly.HasEnoughHistory;
            ScenarioInfo.Severity = bothReady ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            ScenarioInfo.Title = bothReady ? "History-based scenario" : "More history required";
            ScenarioInfo.Message = $"5h: {FormatEstimate(estimate.FiveHour)}\nWeekly: {FormatEstimate(estimate.Weekly)}";
            ScenarioMethodText.Text = estimate.Methodology;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_isLoaded || generation != Volatile.Read(ref _estimateGeneration))
            {
                return;
            }
            ScenarioInfo.IsOpen = true;
            ScenarioInfo.Severity = InfoBarSeverity.Error;
            ScenarioInfo.Title = "Scenario unavailable";
            ScenarioInfo.Message = Summarize(exception.Message);
            ScenarioMethodText.Text = string.Empty;
        }
    }

    private int ParseHistoryDays()
    {
        if (HistoryRangeCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var days))
        {
            return Math.Clamp(days, 1, 3650);
        }
        return 30;
    }

    private static double ReadNumber(NumberBox box, double fallback) =>
        double.IsFinite(box.Value) ? box.Value : fallback;

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatEstimate(ScenarioWindowEstimate estimate)
    {
        if (!estimate.HasEnoughHistory || estimate.ExpectedQuotaDeltaPercent is null)
        {
            return $"not enough data ({estimate.SampleCount} samples) · {estimate.Explanation}";
        }

        return $"{estimate.ExpectedQuotaDeltaPercent:0.#}pp expected " +
               $"[{estimate.LowerQuotaDeltaPercent:0.#}–{estimate.UpperQuotaDeltaPercent:0.#}pp] · " +
               $"confidence {estimate.Confidence:P0} · {estimate.SampleCount} samples";
    }

    private static string FormatSurvivalRate(IReadOnlyList<ForecastSnapshot> snapshots)
    {
        var decided = snapshots.Where(snapshot => snapshot.Forecast.SurvivesUntilReset is not null).ToArray();
        if (decided.Length == 0)
        {
            return "Learning";
        }
        var safe = decided.Count(snapshot => snapshot.Forecast.SurvivesUntilReset == true);
        return $"{100d * safe / decided.Length:0}%";
    }

    private static string FormatKind(QuotaWindowKind kind) =>
        kind == QuotaWindowKind.FiveHour ? "5h" : kind == QuotaWindowKind.Weekly ? "weekly" : kind.ToString();

    private static string Summarize(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 320 ? value : value[..320] + "…";
    }

    private sealed record ForecastRow(string Header, string Detail);
}
