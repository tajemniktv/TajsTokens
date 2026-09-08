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
    private long _evaluationGeneration;

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
        Interlocked.Increment(ref _evaluationGeneration);
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

    private IReadOnlyList<ForecastSnapshot> _history = [];

    private void RenderForecasts(IntelligenceDashboard dashboard)
    {
        _history = dashboard.FiveHourForecasts.Concat(dashboard.WeeklyForecasts)
            .OrderByDescending(x => x.Forecast.GeneratedAtUtc).ToArray();
        RenderLatest(QuotaWindowKind.FiveHour, FiveHourSafeText, FiveHourSamplesText);
        RenderLatest(QuotaWindowKind.Weekly, WeeklySafeText, WeeklySamplesText);
        RenderHistory();
    }

    private void RenderLatest(QuotaWindowKind kind, TextBlock outcome, TextBlock caption)
    {
        var latest = _history.FirstOrDefault(x => x.Forecast.Kind == kind);
        var lane = App.Services.Telemetry.Latest.QuotaLanes.FirstOrDefault(x => x.Kind == kind);
        outcome.Text = lane?.NotReportedByProvider == true ? "Not reported by Codex"
            : latest is null ? "No saved outlook yet" : FormatOutcome(latest.Forecast);
        caption.Text = latest is null ? "History will appear after quota observations are collected."
            : $"Saved {latest.Forecast.GeneratedAtUtc.ToLocalTime():g} · historical projection, not current quota";
    }

    private void OnHistoryRangeChanged(object sender, SelectionChangedEventArgs e) =>
        OnRefreshClicked(sender, new RoutedEventArgs());

    private void OnHistoryFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoaded) RenderHistory();
    }

    private void RenderHistory()
    {
        var selectedIdentity = (ForecastList.SelectedItem as ForecastRow)?.Identity;
        var filtered = _history.Where(x => WindowFilter.SelectedIndex == 0 ||
            x.Forecast.Kind == (WindowFilter.SelectedIndex == 1 ? QuotaWindowKind.FiveHour : QuotaWindowKind.Weekly)).ToArray();
        var visible = HistoryDensity.SelectedIndex == 0
            ? filtered.GroupBy(x => (x.Forecast.Kind, Hour: x.Forecast.GeneratedAtUtc.UtcTicks / TimeSpan.TicksPerHour))
                .Select(g => g.First()).OrderByDescending(x => x.Forecast.GeneratedAtUtc).ToArray()
            : filtered;
        var rows = visible.Select(snapshot =>
        {
            var f = snapshot.Forecast;
            var details = $"Pace: {(f.BurnRatePercentPerHour is double rate ? $"{rate:0.##} quota points/hour" : "not established")}\n" +
                $"Trend: {f.Trend ?? "not established"}\n\n" +
                (f.Evidence is { } evidence
                    ? $"Uncertainty\n{evidence.UncertaintyDescription}\n\nModel: {evidence.Model}\nPolicy: {evidence.PolicyVersion}\n"
                    : "Legacy estimate: no recorded uncertainty method.\n") +
                $"Source: {snapshot.QuotaSource ?? "not recorded"}\n" +
                $"Quota observed: {snapshot.QuotaCapturedAtUtc?.ToLocalTime().ToString("g") ?? "not recorded"}\n" +
                $"Reset: {snapshot.QuotaResetsAtUtc?.ToLocalTime().ToString("g") ?? "not recorded"}";
            return new ForecastRow($"{f.GeneratedAtUtc.ToLocalTime():g} · {FormatKind(f.Kind)}",
                FormatOutcome(f), details, (snapshot.Provider, snapshot.Profile, f.Kind, f.GeneratedAtUtc));
        }).ToArray();
        ForecastList.ItemsSource = rows;
        ForecastList.SelectedItem = rows.FirstOrDefault(x => x.Identity == selectedIdentity) ?? rows.FirstOrDefault();
        if (rows.Length == 0)
        {
            SelectedForecastTitle.Text = "No saved forecasts in this view";
            SelectedForecastOutcome.Text = "—";
            SelectedForecastDetails.Text = "Try another window or a longer history range.";
        }
        StatusText.Text = $"{rows.Length:N0} displayed · {filtered.Length:N0} loaded samples" +
            (HistoryDensity.SelectedIndex == 0 ? " · latest sample per hour and window" : " · every loaded sample") +
            ". Up to 500 recent samples per window.";
    }

    private void OnForecastSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ForecastList.SelectedItem is not ForecastRow row) return;
        SelectedForecastTitle.Text = row.Header;
        SelectedForecastOutcome.Text = row.Detail;
        SelectedForecastDetails.Text = row.Evidence ?? "No additional evidence recorded.";
    }

    private static string FormatOutcome(Forecast f) => f.State switch
    {
        ForecastState.IdleWithinMeterPrecision => "No movement visible at meter precision",
        ForecastState.Learning => "Learning from quota observations",
        _ when f.EstimatedExhaustionAtUtc is DateTimeOffset eta => $"At that pace: exhausted {eta.ToLocalTime():ddd HH:mm}",
        _ when f.ProjectedRemainingAtResetPercent is double left => $"At that pace: ~{left:0.#}% left at reset",
        _ => "Outlook unavailable"
    };

    private async void OnEvaluateClicked(object sender, RoutedEventArgs e)
    {
        var cancellation = _pageCancellation;
        if (!_isLoaded || cancellation is null || cancellation.IsCancellationRequested) return;
        var generation = Interlocked.Increment(ref _evaluationGeneration);
        EvaluateButton.IsEnabled = false;
        EvaluationStatusText.Text = "Replaying authoritative observations with chronological training and source-isolated reset epochs…";
        try
        {
            var now = DateTimeOffset.UtcNow;
            var from = now.AddDays(-ParseHistoryDays());
            var report = await Task.Run(() => App.Services.Intelligence.EvaluateForecastsAsync(
                "codex", "default", from, now, cancellation.Token), cancellation.Token);
            if (!_isLoaded || cancellation.IsCancellationRequested || generation != Volatile.Read(ref _evaluationGeneration)) return;
            EvaluationStatusText.Text = $"{report.AuthoritativeQuotaObservations:N0} authoritative quota observations · {report.WorkloadObservations:N0} native workload observations · {report.TokensWithEffort:N0}/{report.TokenObservations:N0} token records with effort. {report.Methodology}";
            EvaluationList.ItemsSource = report.Scores.Select(score => new ForecastRow(
                $"{FormatKind(score.Kind)} · {score.Target} · {score.Model} · {score.Source} · {score.Origins} origins / {score.ResetGenerations} reset generations",
                score.Origins == 0 ? "No eligible outcomes in this range."
                    : $"MAE {score.MeanAbsoluteError:0.##}pp · RMSE {score.RootMeanSquaredError:0.##}pp · {score.Availability}" +
                      (score.Model.EndsWith("ridge", StringComparison.Ordinal) ? $" · {score.FittedOrigins} fitted origins (others use baseline)" : string.Empty) +
                      (score.IntervalOrigins > 0 ? $" · band coverage {score.IntervalCoverage:P0} on {score.IntervalOrigins} origins; width {score.MeanIntervalWidth:0.#}pp" : " · insufficient interval calibration") +
                      $" · {score.ExhaustionLabels} exhaustion labels ({score.ExhaustionPositiveLabels} positive)" +
                      (score.ExhaustionClassificationAccuracy is double accuracy ? $" · classification accuracy {accuracy:P0}" : string.Empty) +
                      (score.EtaBracketMeanAbsoluteHours is double etaError ? $" · ETA bracket error {etaError:0.##}h on {score.EtaOrigins} origins" : string.Empty))).ToArray();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (_isLoaded && generation == Volatile.Read(ref _evaluationGeneration))
                EvaluationStatusText.Text = $"Evaluation unavailable: {Summarize(exception.Message)}";
        }
        finally
        {
            if (_isLoaded && generation == Volatile.Read(ref _evaluationGeneration)) EvaluateButton.IsEnabled = true;
        }
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
                1,
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

        var range = estimate.LowerQuotaDeltaPercent is not null && estimate.UpperQuotaDeltaPercent is not null
            ? $"[{estimate.LowerQuotaDeltaPercent:0.#}–{estimate.UpperQuotaDeltaPercent:0.#}pp]"
            : "uncertainty learning";
        return $"{estimate.ExpectedQuotaDeltaPercent:0.#}pp conditional estimate · {range} · {estimate.SampleCount} samples";
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

    private sealed record ForecastRow(string Header, string Detail, string? Evidence = null,
        (string Provider, string Profile, QuotaWindowKind Kind, DateTimeOffset GeneratedAtUtc)? Identity = null)
    {
        public override string ToString() => $"{Header} · {Detail}";
    }
}
