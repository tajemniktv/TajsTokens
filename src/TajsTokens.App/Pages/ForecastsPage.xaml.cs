using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

using System.Globalization;

namespace TajsTokens.App.Pages;

public sealed partial class ForecastsPage : Page
{
    private CancellationTokenSource? _pageCancellation;
    private bool _isLoaded;
    private long _loadGeneration;
    private long _estimateGeneration;
    private long _evaluationGeneration;
    private IReadOnlyList<EvaluationRow> _evaluationRows = [];
    private object? _normalTab;
    private bool _modelLab;

    public ForecastsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, e) =>
        {
            var wide = e.NewSize.Width >= (double)Application.Current.Resources["WideContentBreakpoint"];
            HistoryDetailColumn.Width = wide ? new GridLength(1.5, GridUnitType.Star) : new GridLength(0);
            HistoryDetailRow.Height = wide ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(HistoryDetails, wide ? 1 : 0); Grid.SetRow(HistoryDetails, wide ? 0 : 1);
        };
    }

    private App App => (App)Application.Current;

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var normalTab = _normalTab;
        _modelLab = Equals(e.Parameter, "model-lab");
        ForecastTabs.Items.Clear();
        foreach (var tab in _modelLab ? new[] { EvaluationTab } : new[] { OutlookTab, PlanTab, UsageTab, HistoryTab })
            ForecastTabs.Items.Add(tab);
        ForecastTabs.SelectedItem = _modelLab ? EvaluationTab : normalTab ?? OutlookTab;
        PageTitle.Text = _modelLab ? "Model lab" : "Forecasts";
    }

    private void OnModelLabClicked(object sender, RoutedEventArgs e) => App.Navigate(typeof(ForecastsPage), "model-lab");

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
        var current = snapshot.QuotaSnapshots.Where(x => x.Kind is QuotaWindowKind.FiveHour or QuotaWindowKind.Weekly)
            .GroupBy(x => x.Kind).Select(g => g.OrderByDescending(x => x.CapturedAtUtc).First())
            .Where(x => !snapshot.QuotaLanes.Any(l => l.Kind == x.Kind && l.NotReportedByProvider)).ToArray();
        CurrentOutlooks.ItemsSource = current.Select(x =>
        {
            var forecast = snapshot.FindCurrentForecast(x);
            return ViewModels.QuotaCardPresenter.BuildQuotaCard(x.Kind == QuotaWindowKind.Weekly ? "Weekly quota" : "5-hour quota", x,
                forecast is { IsFresh: true } ? forecast.Forecast : null, snapshot.IsQuotaSnapshotFresh(x));
        }).ToArray();
        var trusted = current.Select(snapshot.FindCurrentForecast).Where(x => x is { IsFresh: true }).ToArray();
        var advanced = trusted.Any(x => x?.Forecast?.Evidence?.HorizonPredictions?.Any(h => h.Model.StartsWith("composed-quota/", StringComparison.Ordinal)) == true);
        ModelHealthText.Text = current.Length == 0 ? "No current quota reading. Refresh Codex quota before relying on an outlook." :
            trusted.Length == 0 ? "No fresh outlook is available. Refresh quota and check Diagnostics before relying on a forecast." :
            advanced ? "A workload-based short-term prediction has earned live selection. The outlook above remains conditional on your future activity." :
            "Still learning your next workload. The current outlook keeps its established history-based model until a workload-based prediction proves more reliable on live-collected outcomes. See Model lab for the comparisons.";
        var tokens = snapshot.TokenForecast;
        SessionOutlookText.Text = tokens?.SessionOutlooks is { Count: > 0 } sessions
            ? string.Join("\n\n", sessions.Select(x => $"Next {Horizon(x.HorizonHours)} · " +
                (x.ConditionalMeanTokens is { } mean
                    ? $"if recorded work occurs: mean {CompactTokens(mean)} tokens; historical positive-work 10–90% range {CompactTokens(x.ConditionalLowTokens)}–{CompactTokens(x.ConditionalHighTokens)} (not a calibrated interval)."
                    : "insufficient positive-work history for a conditional estimate.") +
                (x.ActivityEstimateSupported
                    ? $"\nEstimated chance of recorded work: {x.RecordedActivityProbability:P0}; unconditional mean {CompactTokens(x.ExpectedTokens)} tokens."
                    : "\nActivity probability and unconditional mean withheld.") + "\n" + x.Explanation))
            : "Session outlook needs recent activity and enough earlier recorded history. " + tokens?.Activity?.Explanation;
        if (tokens?.IsStale == true) SessionOutlookText.Text = "Stale outlook — refresh failed\n" + SessionOutlookText.Text;
        TokenPredictionText.Text = tokens?.Predictions is { Count: > 0 } predictions
            ? string.Join("\n", predictions.Select(x => $"Next {Horizon(x.HorizonHours)}  ·  ~{CompactTokens(x.ExpectedTokens)} tokens"))
            : tokens?.Activity?.Explanation ?? "No current token workload prediction. Refresh telemetry; this does not require quota-account history.";
        if (tokens?.IsStale == true) TokenPredictionText.Text = "Stale prediction — refresh failed\n" + TokenPredictionText.Text;
        TokenPredictionRange.Text = tokens is not null
            ? string.Join("\n", tokens.Predictions.Select(x => x.LowerTokens is { } low && x.UpperTokens is { } high
                ? $"{Horizon(x.HorizonHours)} range: {CompactTokens(low)}–{CompactTokens(high)} tokens · empirical 80%-target, not a guarantee"
                : $"{Horizon(x.HorizonHours)}: uncertainty still learning")) : string.Empty;
        TokenPredictionEvidence.Text = tokens is not null
            ? $"{tokens.Sessions:N0} sessions · {tokens.TokenEvents:N0} token observations · generated {tokens.GeneratedAtUtc.ToLocalTime():g}\n\n" +
                string.Join("\n\n", tokens.Predictions.Select(x => $"+{x.HorizonHours:0.#}h: {x.Explanation}" +
                    (x.LowerTokens is { } low && x.UpperTokens is { } high ? $" Range: {low:N0}–{high:N0} tokens." : "") +
                    (x.Composition is { } c ? "\nProjected uncached / cache-read / cache-write / output / reasoning: " +
                        string.Join(" / ", c.TokenCategories.Select(v => CompactTokens(v))) +
                        $" tokens. Composition from {c.CompositionObservations} recent observations, not measured future usage." : ""))) +
                "\n\n" + tokens.Methodology + "\n\n" + TajsTokens.Core.Services.SessionWorkloadPredictionService.Methodology
                : "Local token prediction is independent of quota calibration.";
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
        {
            HistorySummary.Visibility = ReferenceEquals(ForecastTabs.SelectedItem, HistoryTab) ? Visibility.Visible : Visibility.Collapsed;
            if (!_modelLab && ForecastTabs.SelectedItem is not null && !ReferenceEquals(ForecastTabs.SelectedItem, EvaluationTab))
                _normalTab = ForecastTabs.SelectedItem;
        }
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

    private async void OnLoadTtHistoryClicked(object sender, RoutedEventArgs e)
    {
        var cancellation = _pageCancellation;
        if (!_isLoaded || cancellation is null || cancellation.IsCancellationRequested) return;
        try
        {
            var history = await App.Services.Intelligence.GetTtEvaluationHistoryAsync("codex", "default", 10, cancellation.Token);
            if (!_isLoaded || cancellation.IsCancellationRequested) return;
            TtHistoryText.Text = history.Count == 0 ? "No saved TT research results yet. Run Model evaluation to save a result locally." :
                "Up to 10 latest original research snapshots; no rescoring or restatement.\n\n" + string.Join("\n\n", history.Select(entry =>
                    entry.Snapshot is { } saved
                        ? $"Saved {saved.RecordedAtUtc:u} · dataset through {saved.DatasetCapturedAtUtc:u} · {saved.Report.Version} · {saved.Id}\n" +
                          $"Requested range {saved.FromUtc:u} to {saved.ToUtc:u}\n" + string.Join("\n", saved.Report.Scores.Select(score =>
                              $"{FormatKind(score.Cohort.Kind)} / {Horizon(score.HorizonHours)} / {QuotaAccountScope.Describe(score.Cohort.AccountKey)} / {score.Cohort.Source}: " +
                              $"Workload {ResearchMetric(score.HeldOutTt, "TT")}; {score.Basis?.BasisId ?? "no basis"}; {score.Status}; " +
                              $"MAE: scalar {ResearchMetric(score.ScalarMae, "pp")}, full vector {ResearchMetric(score.FullVectorMae, "pp")}; {score.HeldOutIntervals} outcomes, {score.UnsupportedIntervals} unsupported. " +
                              FormatTtBias(score) +
                              (score.IsTransfer ? "Transferred basis." : "Local basis.")))
                        : $"Saved {entry.RecordedAtUtc?.ToString("u") ?? "at an unknown time"} · {entry.Id}: unavailable ({entry.Problem}); original row retained."));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (_isLoaded && !cancellation.IsCancellationRequested) TtHistoryText.Text = "Saved TT history unavailable: " + Summarize(exception.Message);
        }
    }

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
            if (report.QuotaCost is { } cost)
            {
                EvaluationMethodText.Text += $"\n\n{cost.Version}: {cost.Methodology}\n" +
                    string.Join(" · ", cost.QualityCounts.Select(x => $"{x.Key}: {x.Value:N0}"));
                if (cost.EvidenceCoverage is { } coverage)
                    EvaluationMethodText.Text += $"\n\nEvidence coverage: {coverage.TokenRecords:N0} token records; {coverage.ReportedTokens:N0} reported tokens. " +
                        $"Model known for {coverage.TokensWithModel:N0} tokens; effort known for {coverage.TokensWithEffort:N0}. " +
                        $"Category mismatch/invalid records: {coverage.CategoryMismatchRecords:N0}; collection time unknown: {coverage.UnknownCollectionRecords:N0}; collected after event: {coverage.CollectedAfterEventRecords:N0}. " +
                        "Requested-tier settings: " + string.Join(", ", coverage.RequestedTierSettings.Select(x => $"{x.Key}={x.Value:N0}")) +
                        $"; missing tier={coverage.MissingTierSettings:N0}; undated settings={coverage.UndatedTierSettings:N0}.\n" + QuotaEvaluationCoverageBuilder.Boundary;
            }
            if (report.ComposedQuota is { } composed) EvaluationMethodText.Text += "\n\n" + composed.Methodology;
            if (report.ComposedQuotaStrict is { } strict) EvaluationMethodText.Text += "\n\n" + strict.Methodology;
            if (report.Tt is { } tt) EvaluationMethodText.Text += "\n\n" + tt.Methodology +
                (tt.Scores.Any(x => x.IsTransfer) ? "" : " No chronological compatible TT transfer pairs in this range; cross-regime scaling remains unvalidated.");
            if (report.SavedTtSnapshotId is { } savedTtId) EvaluationMethodText.Text += $"\nOriginal TT research snapshot saved locally: {savedTtId}. Later runs are separate reconstructions, not updates to this result.";
            if (report.QuotaTransfer is { } transfer) EvaluationMethodText.Text += "\n\n" + transfer.Methodology +
                (transfer.Scores.Count == 0 ? " No compatible recorded-account regimes in this range; transfer and TT remain unsupported." : "");
            if (report.SessionScores.Count > 0) EvaluationMethodText.Text += "\n\n" + TajsTokens.Core.Services.SessionWorkloadPredictionService.Methodology + "\n" +
                string.Join("\n", report.SessionScores.Select(x => $"Session {Horizon(x.HorizonHours)}: {x.Origins} activity outcomes ({x.ActiveOrigins} positive); Brier {x.BrierScore:0.000} vs global frequency {x.BaselineBrierScore:0.000}; calibration error {x.CalibrationError:0.000}. " +
                    $"Conditional MAE {CompactTokens(x.ConditionalMeanAbsoluteError)} vs pace {CompactTokens(x.ConditionalPaceMeanAbsoluteError)} on {x.ConditionalOrigins} positive outcomes; historical-range coverage {x.ConditionalRangeCoverage:P0}. " +
                    $"Unconditional MAE {CompactTokens(x.ExpectedMeanAbsoluteError)} vs pace {CompactTokens(x.PaceMeanAbsoluteError)} tokens on {x.ExpectedOrigins} matched outcomes."));
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
                .Concat((report.QuotaCost?.ConstructionCoverage ?? []).Select(coverage => new EvaluationRow(
                    $"Evidence construction · {FormatKind(coverage.Cohort.Kind)} · {Horizon(coverage.HorizonHours)} · {coverage.Cohort.Source} · {QuotaAccountScope.Describe(coverage.Cohort.AccountKey)} · {QuotaHistoryPolicy.DescribeCohort(coverage.Cohort)}",
                    "Target selection, not a model",
                    $"{coverage.BuiltIntervals:N0} built intervals from {coverage.CandidateStarts:N0} candidate starts",
                    "First rejection reasons: " + string.Join("; ", coverage.RejectedStarts.Select(x => $"{x.Key}={x.Value:N0}")) +
                    ". " + QuotaCostObservationBuilder.CoverageBoundary + "\n" + FormatStrictCoverage(report, coverage))))
                .Concat((report.QuotaCost?.CohortCoverage ?? []).Select(coverage => new EvaluationRow(
                    $"Evidence coverage · {FormatKind(coverage.Cohort.Kind)} · {Horizon(coverage.HorizonHours)} · {coverage.Cohort.Source} · {QuotaAccountScope.Describe(coverage.Cohort.AccountKey)} · {QuotaHistoryPolicy.DescribeCohort(coverage.Cohort)}",
                    "Coverage, not a model",
                    $"{coverage.Intervals:N0} built intervals; {coverage.NativeAccountIntervals:N0} with native quota account ID; {coverage.AssertedAccountIntervals:N0} with ownership assertion",
                    "Quota identity does not attribute every local token to that account. Overlapping flags: " +
                    string.Join("; ", coverage.QualityCounts.Select(x => $"{x.Key}={x.Value:N0}")))))
                .Concat((report.QuotaCost?.Scores ?? []).Select(score => new EvaluationRow(
                    $"Cost calibration (actual work, not forecast) · {FormatKind(score.Cohort.Kind)} · {Horizon(score.HorizonHours)} · {score.Cohort.Source} · {QuotaAccountScope.Describe(score.Cohort.AccountKey)} · {QuotaHistoryPolicy.DescribeCohort(score.Cohort)}",
                    score.Candidate,
                    score.Status == "no-recorded-training-work" ? "Cost unavailable: no recorded workload in frozen training" :
                    score.HeldOutSamples == 0 ? "Insufficient independent history for held-out cost calibration" :
                        $"Interval loss {score.IntervalLoss:0.###}pp · {score.HeldOutSamples:N0} held-out intervals · {score.EvidenceSupport.EvaluationBlocks} blocks / {score.HeldOutGenerations} resets · ESS estimate {score.EvidenceSupport.EffectiveSampleSize:0.#}",
                    $"{score.Status} · training {score.TrainingSamples} intervals / {score.TrainingGenerations} generations. " +
                    $"Incomplete category evidence: {score.IncompleteCategoryTrainingIntervals} training / {score.IncompleteCategoryHeldOutIntervals} held-out intervals withheld from category candidates. " +
                    $"Displayed-delta MAE {ResearchMetric(score.DisplayedDeltaMae, "pp")}; block-average interval loss {ResearchMetric(score.BlockMeanIntervalLoss, "pp")}. " +
                    $"Residual p10 / median / p90: {ResearchMetric(score.ResidualP10, "pp")} / {ResearchMetric(score.ResidualMedian, "pp")} / {ResearchMetric(score.ResidualP90, "pp")}. " +
                    $"Mean unexplained movement above envelope lower bound: {ResearchMetric(score.UnexplainedPositiveMovement, "pp")}. Not causal attribution. " +
                    $"{score.CandidateShiftTimes.Count} candidate residual shifts; no automatic alerts or retraining. " +
                    (score.BandSamples > 0 ? $"Band/target intersection {score.BandIntersectsTargetRate:P0} on {score.BandSamples} intervals; not latent coverage. " : "Insufficient generations for bands. ") +
                    (score.RateCardVersion is null ? "" :
                        $"API-price baseline {score.RateCardVersion}: {score.UnpricedTrainingIntervals} unpriced training / {score.UnpricedHeldOutIntervals} unpriced held-out intervals; {score.UnpricedReportedTokens:N0} unpriced reported tokens. " +
                        $"Matched-outcome pace/total losses {score.PairedPaceIntervalLoss:0.###}/{score.PairedTotalIntervalLoss:0.###}pp. Fixed standard/short-context weights, not actual spend or credits; no tier, historical-price or long-context claim. ") +
                    "Frozen coefficients: " + string.Join("; ", score.Coefficients.Select(x => $"{x.Key}={x.Value:0.####}")))))
                .Concat((report.ComposedQuota?.Scores ?? []).Concat(report.ComposedQuotaStrict?.Scores ?? []).Select(score => new EvaluationRow(
                    $"End-to-end quota forecast · {score.Availability} · {FormatKind(score.Cohort.Kind)} · {Horizon(score.HorizonHours)} · {score.Cohort.Source} · {QuotaAccountScope.Describe(score.Cohort.AccountKey)} · {QuotaHistoryPolicy.DescribeCohort(score.Cohort)}",
                    score.CostModel,
                    score.WithheldReasons.ContainsKey("no-recorded-training-work") ? "Cost unavailable: no recorded workload in frozen training" :
                    score.HeldOutIntervals == 0 ? "Insufficient chronological history" :
                        $"Forecast interval loss {score.IntervalLoss:0.###}pp · {score.HeldOutIntervals} held-out intervals / {score.EvidenceSupport.EvaluationBlocks} blocks / {score.ResetGenerations} resets · ESS estimate {score.EvidenceSupport.EffectiveSampleSize:0.#}",
                    ComposedQuotaPolicy.AssessSelection(score, report.EvaluatedAtUtc,
                        score.Availability == ForecastReplayAvailability.CollectedByOrigin).Explanation + "\n\n" +
                    $"Actual-work cost loss {ResearchMetric(score.CostOnlyIntervalLoss, "pp")} versus forecast loss {ResearchMetric(score.IntervalLoss, "pp")}. " +
                    (score.IntervalOrigins > 0 ? $"Joint range: {score.IntervalCoverage:P0} reported-value coverage on {score.IntervalOrigins} origins; mean width {score.MeanIntervalWidth:0.###}pp. "
                        : "Joint range unavailable: insufficient earlier completed reset calibration. ") +
                    $"Pace {ResearchMetric(score.PaceIntervalLoss, "pp")}; incumbent policy {ResearchMetric(score.IncumbentIntervalLoss, "pp")}; displayed-delta MAE {ResearchMetric(score.DisplayedDeltaMae, "pp")}. " +
                    $"Training: {score.AssertedTrainingIntervals} user-asserted intervals; remaining training and all validation are native cohort evidence. " +
                    $"{score.MissingComposition} intervals withheld for missing/stale composition or unavailable training/meters. " +
                    (score.WithheldReasons.Count == 0 ? "" : "Reasons (may overlap): " + string.Join("; ", score.WithheldReasons.Select(x => $"{x.Key}: {x.Value}")) + ". ") +
                    (score.Availability == ForecastReplayAvailability.CollectedByOrigin ? report.ComposedQuotaStrict!.Methodology : report.ComposedQuota!.Methodology) +
                    "\n\n" + FormatComposedBreakdowns(score))))
                .Concat((report.SessionQuota?.Scores ?? []).Select(score => new EvaluationRow(
                    $"Session quota research · {FormatKind(score.Cohort.Kind)} · {Horizon(score.HorizonHours)} · {score.Cohort.Source} · {QuotaAccountScope.Describe(score.Cohort.AccountKey)} · {QuotaHistoryPolicy.DescribeCohort(score.Cohort)}",
                    "activity × conditional workload × frozen total-token cost",
                    $"{score.Trials.Count} outcomes / {score.ResetGenerations} resets; expected MAE {ResearchMetric(score.ExpectedMae, "pp")} vs pace {ResearchMetric(score.PaceMae, "pp")}",
                    $"Conditional MAE {ResearchMetric(score.ConditionalMae, "pp")} vs matched pace {ResearchMetric(score.ConditionalPaceMae, "pp")} on {score.ActiveOutcomes} positive-work outcomes. " +
                    $"Expected interval loss {ResearchMetric(score.IntervalLoss, "pp")} vs pace {ResearchMetric(score.PaceIntervalLoss, "pp")}. {score.TrainingIntervals} training intervals; {score.WithheldIntervals} withheld. " +
                    (score.TrainingIssue is { } issue ? $"Cost training unavailable: {issue}. No recorded work is not evidence of free work or complete collection. " : "") +
                    (score.IncumbentPairedOrigins > 0
                        ? $"Matched incumbent comparison ({score.IncumbentPairedOrigins} outcomes): expected/incumbent MAE {score.PairedExpectedMae:0.###}/{score.IncumbentMae:0.###}pp; interval loss {score.PairedExpectedIntervalLoss:0.###}/{score.IncumbentIntervalLoss:0.###}pp. "
                        : "No identical incumbent origin/outcome pairs; no comparison claimed. ") +
                    (score.BandOrigins > 0 ? $"Joint band coverage {score.ReportedBandCoverage:P0} on {score.BandOrigins} outcomes; mean width {score.MeanBandWidth:0.###}pp. " : "Insufficient completed resets for joint bands. ") +
                    report.SessionQuota!.Methodology)))
                .Concat((report.Tt?.Scores ?? []).Select(score => new EvaluationRow(
                    $"TT {(score.IsTransfer ? "transfer" : "local")} research · {FormatKind(score.Cohort.Kind)} · {Horizon(score.HorizonHours)} · {score.Cohort.Source} · {QuotaAccountScope.Describe(score.Cohort.AccountKey)} · {QuotaHistoryPolicy.DescribeCohort(score.Cohort)}",
                    score.Basis?.BasisId ?? "No scoring basis",
                    $"{score.Status} · {score.HeldOutIntervals} supported outcomes / {score.ResetGenerations} resets",
                    $"Basis/calibration intervals {score.BasisIntervals}/{score.CalibrationIntervals}. Unsupported held-out work: {score.UnsupportedIntervals} intervals / {CompactTokens(score.UnsupportedTokens)} tokens. " +
                    $"Basis context {QuotaHistoryPolicy.DescribeCohort(score.BasisCohort)}; basis ends {score.BasisEndUtc?.ToString("u") ?? "unavailable"}, calibration ends {score.CalibrationEndUtc?.ToString("u") ?? "unavailable"}. " +
                    $"Supported held-out workload {ResearchMetric(score.HeldOutTt, "TT")}; separate calibration {ResearchMetric(score.QuotaPointsPerTt, "pp/TT", "0.######")}. " +
                    $"Matched block-balanced loss: TT scalar {ResearchMetric(score.ScalarLoss, "pp")}, full vector {ResearchMetric(score.FullVectorLoss, "pp")}, raw tokens {ResearchMetric(score.RawTokenLoss, "pp")}. " +
                    $"Zero-use loss {ResearchMetric(score.ZeroLoss, "pp")}; displayed-delta MAE: TT {ResearchMetric(score.ScalarMae, "pp")}, full {ResearchMetric(score.FullVectorMae, "pp")}, raw {ResearchMetric(score.RawTokenMae, "pp")}. " +
                    FormatTtBias(score) +
                    (score.Basis is { } basis ? "Category weights/token: " + string.Join(", ", basis.Weights.Select(x => x.ToString("G6", CultureInfo.InvariantCulture))) +
                        "; 1-TT reference category counts: " + string.Join(", ", basis.Reference.Select(x => x.ToString("G6", CultureInfo.InvariantCulture))) +
                        "; supported models: " + string.Join(", ", basis.Models) + "; efforts: " + string.Join(", ", basis.Efforts) + ". " : "") +
                    report.Tt!.Methodology)))
                .Concat((report.QuotaTransfer?.Scores ?? []).Select(score => new EvaluationRow(
                    $"Regime transfer research · {Horizon(score.HorizonHours)} · {score.Source.Source} · {QuotaHistoryPolicy.DescribeCohort(score.Source)} → {QuotaHistoryPolicy.DescribeCohort(score.Destination)}",
                    score.Model,
                    $"{score.Status} · {score.HeldOut} held-out intervals / {score.HeldOutGenerations} resets",
                    $"Source training {score.SourceTraining}; destination training {score.DestinationTraining}. " +
                    $"Unscaled loss {score.UnscaledLoss:0.###}pp; scale-only loss {score.ScaledLoss:0.###}pp; local-only loss {score.LocalOnlyLoss:0.###}pp. " +
                    $"Fitted destination scale {score.Scale:0.####}. {report.QuotaTransfer!.Methodology}")))
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

    private static string FormatComposedBreakdowns(ComposedQuotaScore score)
    {
        if (score.Breakdowns.Count == 0) return "No held-out breakdowns available.";
        string Number(double? value) => value is { } n ? $"{n:0.###}" : "unavailable";
        return "Held-out diagnostic partitions (not model selection):\n" + string.Join("\n", score.Breakdowns.Take(60).Select(x =>
            $"{x.Dimension} / {x.Group}: {x.Outcomes} outcomes, {x.ResetGenerations} resets; " +
            $"forecast/cost-only/pace loss {x.ForecastIntervalLoss:0.###}/{x.CostOnlyIntervalLoss:0.###}/{x.PaceIntervalLoss:0.###}pp; bias {x.SignedBias:+0.###;-0.###;0}pp. " +
            $"Matched forecast/incumbent {Number(x.PairedForecastIntervalLoss)}/{Number(x.IncumbentIntervalLoss)}pp ({x.IncumbentPairs}); " +
            $"matched forecast/zero-use {Number(x.ZeroPairedForecastIntervalLoss)}/{Number(x.ZeroUseIntervalLoss)}pp ({x.ZeroUsePairs}). " +
            $"Below-meter underprediction {x.UnderpredictedOutcomes}/{x.MeteredOutcomes}; mean {Number(x.MeanUnderprediction)}pp (includes zero misses). " +
            (x.BandOutcomes > 0 ? $"Band coverage {x.BandCoverage:P0}, width {x.MeanBandWidth:0.###}pp ({x.BandOutcomes})." : "No evaluated band outcomes."))) +
            (score.Breakdowns.Count > 60 ? $"\n{score.Breakdowns.Count - 60} additional groups omitted here; full breakdowns remain in the evaluation CLI output." : "");
    }

    private static string FormatStrictCoverage(ForecastEvaluationReport report, QuotaCostConstructionCoverage coverage)
    {
        var scores = report.ComposedQuotaStrict?.Scores.Where(x => x.Cohort == coverage.Cohort &&
            x.HorizonHours == coverage.HorizonHours).ToArray() ?? [];
        return scores.Length == 0 ? "Strict replay: no matching comparison; not evidence of zero exclusions." :
            "Strict replay (per candidate; counts overlap, do not sum): " + string.Join(" | ", scores.Select(x =>
                $"{x.CostModel}: {x.TrainingIntervals} training, {x.HeldOutIntervals} evaluated; " +
                string.Join(", ", x.WithheldReasons.Select(y => $"{y.Key}={y.Value}"))));
    }

    private static string ResearchMetric(double? value, string unit, string format = "0.###") =>
        value is double number && double.IsFinite(number)
            ? $"{number.ToString(format, CultureInfo.CurrentCulture)} {unit}"
            : "unavailable";

    private static string FormatTtBias(TtEvaluationScore score) => score.ScalarBias is null
        ? "Signed error unavailable. "
        : $"Signed bias {ResearchMetric(score.ScalarBias, "pp", "+0.###;-0.###;0")} (positive = overestimate); eligible cumulative error {ResearchMetric(score.CumulativeError, "pp", "+0.###;-0.###;0")} " +
          $"[{ResearchMetric(score.CumulativeErrorLower, "pp")}, {ResearchMetric(score.CumulativeErrorUpper, "pp")}] meter envelope, not confidence. ";

    private static string Horizon(double hours) => hours < 1 ? $"{hours * 60:0} min" : $"{hours:0.#}h";

    private static string CompactTokens(double? value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000:0.##}B",
        >= 1_000_000 => $"{value / 1_000_000:0.##}M",
        >= 1_000 => $"{value / 1_000:0.##}K",
        null => "unavailable",
        _ => $"{value:0}"
    };

    private async void OnRecentPatternClicked(object sender, RoutedEventArgs e)
    {
        var cancellation = _pageCancellation;
        if (!_isLoaded || cancellation is null || cancellation.IsCancellationRequested) return;
        var original = (RootAgentsBox.Value, SubagentsBox.Value, ModelBox.Text, ReasoningBox.Text);
        RecentPatternButton.IsEnabled = false;
        RecentPatternText.Text = "Reading recent local activity…";
        try
        {
            var pattern = await App.Services.Intelligence.GetRecentScenarioPatternAsync(DateTimeOffset.UtcNow, cancellation.Token);
            if (!_isLoaded || cancellation.IsCancellationRequested) return;
            if (original != (RootAgentsBox.Value, SubagentsBox.Value, ModelBox.Text, ReasoningBox.Text))
            {
                RecentPatternText.Text = "Inputs changed while loading; your edits were preserved. Click again to load a recent pattern.";
                return;
            }
            if (pattern.Available && (pattern.RootSessions > RootAgentsBox.Maximum || pattern.SubagentSessions > SubagentsBox.Maximum))
            {
                RecentPatternText.Text = "Recent session counts exceed this planner's input range; no values were silently clamped. Enter a hypothetical workload manually.";
                return;
            }
            RecentPatternText.Text = pattern.Explanation;
            if (!pattern.Available) return;
            RootAgentsBox.Value = pattern.RootSessions;
            SubagentsBox.Value = pattern.SubagentSessions;
            ModelBox.Text = pattern.Model ?? string.Empty;
            ReasoningBox.Text = pattern.ReasoningEffort ?? string.Empty;
            Interlocked.Increment(ref _estimateGeneration);
            ScenarioInfo.Severity = InfoBarSeverity.Informational;
            ScenarioInfo.Title = "Review the recent pattern";
            ScenarioInfo.Message = "Inputs updated, not an estimate. Choose a duration and select Estimate quota use.";
            ScenarioMethodText.Text = string.Empty;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (_isLoaded && !cancellation.IsCancellationRequested) RecentPatternText.Text = "Recent pattern unavailable: " + Summarize(exception.Message);
        }
        finally { RecentPatternButton.IsEnabled = true; }
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
