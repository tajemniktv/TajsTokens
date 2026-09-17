using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.App.Pages;

public sealed partial class ForecastsPage : Page
{
    private CancellationTokenSource? _pageCancellation;
    private bool _isLoaded;
    private long _loadGeneration;
    private long _estimateGeneration;
    private long _evaluationGeneration;
    private IReadOnlyList<EvaluationRow> _evaluationRows = [];

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
        QuotaHistoryText.Text = dashboard.QuotaHistorySummary ?? "Historical quota evidence is unavailable.";
        var snapshot = App.Services.Telemetry.Latest;
        var tokens = snapshot.TokenForecast;
        TokenPredictionText.Text = tokens?.Predictions is { Count: > 0 } predictions
            ? string.Join("\n", predictions.Select(x => $"Next {Horizon(x.HorizonHours)}  ·  ~{CompactTokens(x.ExpectedTokens)} tokens"))
            : "No current token workload prediction. Refresh telemetry; this does not require quota-account history.";
        if (tokens?.IsStale == true) TokenPredictionText.Text = "Stale prediction — refresh failed\n" + TokenPredictionText.Text;
        TokenPredictionRange.Text = tokens is not null
            ? string.Join("\n", tokens.Predictions.Select(x => x.LowerTokens is { } low && x.UpperTokens is { } high
                ? $"{Horizon(x.HorizonHours)} range: {CompactTokens(low)}–{CompactTokens(high)} tokens · empirical 80%-target, not a guarantee"
                : $"{Horizon(x.HorizonHours)}: uncertainty still learning")) : string.Empty;
        TokenPredictionEvidence.Text = tokens is not null
            ? $"{tokens.Sessions:N0} sessions · {tokens.TokenEvents:N0} token observations · generated {tokens.GeneratedAtUtc.ToLocalTime():g}\n\n" +
                string.Join("\n\n", tokens.Predictions.Select(x => $"+{x.HorizonHours:0.#}h: {x.Explanation}" +
                    (x.LowerTokens is { } low && x.UpperTokens is { } high ? $" Range: {low:N0}–{high:N0} tokens." : ""))) +
                "\n\n" + tokens.Methodology : "Local token prediction is independent of quota calibration.";
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
        caption.Text = lane?.NotReportedByProvider == true ? "This window is absent from the current provider response. Saved projections remain in history below."
            : latest is null ? "History will appear after quota observations are collected."
            : $"Saved {latest.Forecast.GeneratedAtUtc.ToLocalTime():g} · {QuotaAccountScope.Describe(latest.AccountKey)} · historical projection, not current quota";
    }

    private void OnHistoryRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        Interlocked.Increment(ref _evaluationGeneration);
        _evaluationRows = [];
        EvaluationGroup.ItemsSource = null;
        EvaluationList.ItemsSource = null;
        EvaluateButton.IsEnabled = true;
        EvaluationStatusText.Text = "History range changed. Run evaluation for this range.";
        EvaluationMethodText.Text = "No evaluation has run for the selected range.";
        EvaluationSelectionText.Text = "No evaluation results yet.";
        OnRefreshClicked(sender, new RoutedEventArgs());
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistorySummary is not null && ForecastTabs is not null)
            HistorySummary.Visibility = ForecastTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

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
            ? filtered.GroupBy(x => (x.Provider, x.Profile, x.Forecast.Kind, x.AccountKey, x.QuotaSource, x.QuotaWindowMinutes,
                    x.Forecast.Evidence?.AnchorLimitId, x.Forecast.Evidence?.AnchorPlanType,
                    Hour: x.Forecast.GeneratedAtUtc.UtcTicks / TimeSpan.TicksPerHour))
                .Select(g => g.First()).OrderByDescending(x => x.Forecast.GeneratedAtUtc).ToArray()
            : filtered;
        var rows = visible.Select(snapshot =>
        {
            var f = snapshot.Forecast;
            var details = $"Pace: {(f.BurnRatePercentPerHour is double rate ? $"{rate:0.##} quota points/hour" : "not established")}\n" +
                $"Trend: {f.Trend ?? "not established"}\n\n" +
                (f.Evidence is { } evidence
                    ? $"Uncertainty\n{evidence.UncertaintyDescription}\n\nModel: {evidence.Model}\nPolicy: {evidence.PolicyVersion}\n" +
                        $"Bucket: {evidence.AnchorLimitId ?? "not recorded"} · plan: {evidence.AnchorPlanType ?? "not recorded"}\nHistory policy: {evidence.HistoryPolicy ?? "legacy"}\n"
                    : "Legacy estimate: no recorded uncertainty method.\n") +
                (f.Evidence?.HorizonPredictions is { Count: > 0 } horizons
                    ? string.Join("\n\n", horizons.Select(x => $"+{x.HorizonHours:0.#}h: ~{x.RemainingPercent:0.#}% remaining · {x.Model}\n{x.Explanation}")) + "\n\n"
                    : "") +
                $"{f.Evidence?.WorkloadStatus}\nSource: {snapshot.QuotaSource ?? "not recorded"}\n{QuotaAccountScope.Describe(snapshot.AccountKey)}\n" +
                $"Quota observed: {snapshot.QuotaCapturedAtUtc?.ToLocalTime().ToString("g") ?? "not recorded"}\n" +
                $"Native window: {snapshot.QuotaWindowMinutes?.ToString() ?? "not recorded"} minutes\n" +
                $"Reset: {snapshot.QuotaResetsAtUtc?.ToLocalTime().ToString("g") ?? "not recorded"}";
            var summary = $"{QuotaAccountScope.Describe(snapshot.AccountKey)}\n" +
                $"Pace: {(f.BurnRatePercentPerHour is double pace ? $"{pace:0.##} quota points/hour" : "not established")}\n" +
                $"Reset: {snapshot.QuotaResetsAtUtc?.ToLocalTime().ToString("g") ?? "not recorded"}\n\n" +
                (f.Evidence?.HorizonPredictions is { Count: > 0 } outlooks
                    ? string.Join("\n", outlooks.Select(x => $"In {Horizon(x.HorizonHours)}: ~{x.RemainingPercent:0.#}% remaining" +
                        (x.LowerRemainingPercent is { } low && x.UpperRemainingPercent is { } high
                            ? $" · 80%-target range {low:0.#}–{high:0.#}%" : " · uncertainty learning")))
                    : f.Evidence?.UncertaintyDescription ?? "No uncertainty method recorded.");
            return new ForecastRow($"{f.GeneratedAtUtc.ToLocalTime():g} · {FormatKind(f.Kind)}",
                FormatOutcome(f), details, (snapshot.Provider, snapshot.Profile, f.Kind, f.GeneratedAtUtc, snapshot.AccountKey),
                QuotaAccountScope.Describe(snapshot.AccountKey), summary);
        }).ToArray();
        ForecastList.ItemsSource = rows;
        ForecastList.SelectedItem = rows.FirstOrDefault(x => x.Identity == selectedIdentity) ?? rows.FirstOrDefault();
        if (rows.Length == 0)
        {
            SelectedForecastTitle.Text = "No saved forecasts in this view";
            SelectedForecastOutcome.Text = "—";
            SelectedForecastDetails.Text = "Try another window or a longer history range.";
            SelectedForecastSummary.Text = string.Empty;
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
        SelectedForecastSummary.Text = row.Summary ?? string.Empty;
    }

    private static string FormatOutcome(Forecast f) => f.State switch
    {
        ForecastState.IdleWithinMeterPrecision => "Below meter precision",
        ForecastState.Learning => "Learning from quota observations",
        _ when f.EstimatedExhaustionAtUtc is DateTimeOffset eta => $"Projected exhaustion: {eta.ToLocalTime():g}",
        _ when f.ProjectedRemainingAtResetPercent is double left => $"At that pace: ~{left:0.#}% left at reset",
        _ => "Outlook unavailable"
    };

    private async void OnEvaluateClicked(object sender, RoutedEventArgs e)
    {
        var cancellation = _pageCancellation;
        if (!_isLoaded || cancellation is null || cancellation.IsCancellationRequested) return;
        var generation = Interlocked.Increment(ref _evaluationGeneration);
        EvaluateButton.IsEnabled = false;
        EvaluationStatusText.Text = "Replaying historical quota with chronological training and source/account/plan-isolated cohorts…";
        try
        {
            var now = DateTimeOffset.UtcNow;
            var from = now.AddDays(-ParseHistoryDays());
            var report = await Task.Run(() => App.Services.Intelligence.EvaluateForecastsAsync(
                "codex", "default", from, now, cancellation.Token), cancellation.Token);
            if (!_isLoaded || cancellation.IsCancellationRequested || generation != Volatile.Read(ref _evaluationGeneration)) return;
            EvaluationStatusText.Text = $"Evaluated {from.ToLocalTime():d}–{now.ToLocalTime():d} · {report.Scores.Count:N0} quota comparisons · {report.TokenScores.Count:N0} token comparisons. Select a cohort below; lower error is better within the same target.";
            EvaluationMethodText.Text = $"{report.QuotaHistorySummary} {report.WorkloadObservations:N0} native workload observations · {report.TokensWithEffort:N0}/{report.TokenObservations:N0} token records with effort. {report.Methodology}";
            _evaluationRows = report.Scores.Select(score => new EvaluationRow(
                $"{FormatKind(score.Kind)} · {score.Target} · {score.Source} · {QuotaAccountScope.Describe(score.AccountKey)} · {QuotaHistoryPolicy.DescribeCohort(score.HistoryCohort)} · {score.Availability}",
                score.Model,
                score.Origins == 0 ? "No eligible outcomes" : $"Mean error {score.MeanAbsoluteError:0.##} quota points · {score.Origins:N0} origins · {score.ResetGenerations} reset generations",
                score.Origins == 0 ? "No eligible outcomes in this range."
                    : $"MAE {score.MeanAbsoluteError:0.##}pp · RMSE {score.RootMeanSquaredError:0.##}pp · {score.Availability} · {QuotaHistoryPolicy.DescribeCohort(score.HistoryCohort)}" +
                      (score.Model.EndsWith("ridge", StringComparison.Ordinal) ? $" · {score.FittedOrigins} fitted origins (others use baseline)" : string.Empty) +
                      (score.IntervalOrigins > 0 ? $" · band coverage {score.IntervalCoverage:P0} on {score.IntervalOrigins} origins; width {score.MeanIntervalWidth:0.#}pp" : " · insufficient interval calibration") +
                      $" · {score.NonOverlappingOrigins} non-overlapping origins within cohort · {score.ExhaustionLabels} exhaustion labels ({score.ExhaustionPositiveLabels} positive)" +
                      (score.ExhaustionClassificationAccuracy is double accuracy ? $" · classification accuracy {accuracy:P0}" : string.Empty) +
                      (score.EtaBracketMeanAbsoluteHours is double etaError ? $" · ETA bracket error {etaError:0.##}h on {score.EtaOrigins} origins" : string.Empty)))
                .Concat(report.TokenScores.Select(score => new EvaluationRow(
                    $"Recorded tokens · {Horizon(score.HorizonHours)} · reconstructed rollout history",
                    score.Model,
                    $"Mean error {CompactTokens(score.MeanAbsoluteError)} tokens · {score.Origins:N0} held-out origins",
                    $"MAE {CompactTokens(score.MeanAbsoluteError)} tokens · RMSE {CompactTokens(score.RootMeanSquaredError)} tokens · reconstructed rollout history · {score.WorkloadOrigins} workload-model predictions" +
                    (score.IntervalOrigins > 0 ? $" · band coverage {score.IntervalCoverage:P0} on {score.IntervalOrigins} origins" : ""))))
                .ToArray();
            EvaluationGroup.ItemsSource = _evaluationRows.Select(x => x.Group).Distinct().OrderBy(x => x).ToArray();
            EvaluationGroup.SelectedIndex = _evaluationRows.Count > 0 ? 0 : -1;
            RenderEvaluationGroup();
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

    private void OnEvaluationGroupChanged(object sender, SelectionChangedEventArgs e) => RenderEvaluationGroup();

    private void RenderEvaluationGroup()
    {
        if (EvaluationList is null) return;
        var group = EvaluationGroup.SelectedItem as string;
        var rows = _evaluationRows.Where(x => x.Group == group).ToArray();
        EvaluationList.ItemsSource = rows;
        EvaluationSelectionText.Text = group is null ? "No evaluation results in this range."
            : $"{rows.Length} models · {group}\nComparisons are scoped to this cohort. Counts are not additive across overlapping histories.";
    }

    private static string Horizon(double hours) => hours < 1 ? $"{hours * 60:0} min" : $"{hours:0.#}h";

    private static string CompactTokens(double? value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000:0.##}B",
        >= 1_000_000 => $"{value / 1_000_000:0.##}M",
        >= 1_000 => $"{value / 1_000:0.##}K",
        null => "unavailable",
        _ => $"{value:0}"
    };

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
            var accounts = App.Services.Telemetry.Latest.QuotaLanes.Where(lane => lane.IsFresh)
                .Select(lane => lane.Snapshot?.AccountKey).Distinct().ToArray();
            if (accounts.Length != 1 || accounts[0] is null)
            {
                ScenarioInfo.IsOpen = true;
                ScenarioInfo.Severity = InfoBarSeverity.Warning;
                ScenarioInfo.Title = "Current quota account unavailable";
                ScenarioInfo.Message = "Refresh quota and retry. A scenario needs a fresh Codex response with a known backend account; saved history alone cannot establish the current account.";
                ScenarioMethodText.Text = string.Empty;
                return;
            }
            var request = new ScenarioRequest(
                ReadNumber(DurationBox, 2),
                (int)Math.Round(ReadNumber(RootAgentsBox, 1)),
                (int)Math.Round(ReadNumber(SubagentsBox, 0)),
                1,
                NullIfWhiteSpace(ModelBox.Text),
                NullIfWhiteSpace(ReasoningBox.Text),
                accounts[0]);
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
            var anyReady = estimate.FiveHour.HasEnoughHistory || estimate.Weekly.HasEnoughHistory;
            ScenarioInfo.Severity = bothReady ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            ScenarioInfo.Title = bothReady ? "History-based scenario"
                : anyReady ? "Scenario available for one window" : "Scenario unavailable for this request";
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
            return $"unavailable · {estimate.Explanation}";
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
        (string Provider, string Profile, QuotaWindowKind Kind, DateTimeOffset GeneratedAtUtc, string? AccountKey)? Identity = null,
        string? Scope = null, string? Summary = null)
    {
        public override string ToString() => $"{Header} · {Detail}";
    }

    private sealed record EvaluationRow(string Group, string Header, string Metric, string Detail);
}
