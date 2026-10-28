// Taj's Tokens | CodexIntelligencePage.xaml.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;

#endregion

namespace TajsTokens.App.Pages;

public sealed partial class CodexIntelligencePage : Page, IHistoryPresentation
{
    private bool _loaded;
    private bool _populatingAccounts;
    private string? SelectedAccountKey => (Account.SelectedItem as AccountOption)?.Key;
    private CancellationTokenSource? _request;
    private CodexIntelligenceSnapshot? _snapshot;
    private SqliteReadGeneration? _historyGeneration;
    private RuntimeSettings? _historySettings;
    private DateTimeOffset _historyLoadedAt;
    private int _telemetryRenderQueued;
    public long HistoryReadCount { get; private set; }
    public long HistoryCacheHits { get; private set; }

    public CodexIntelligencePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;
        FromDate.Date = DateTimeOffset.UtcNow.AddDays(-6);
        ToDate.Date = DateTimeOffset.UtcNow.AddDays(1);
        Loaded += async (_, _) =>
        {
            _loaded = true;
            App.Services.Telemetry.SnapshotUpdated += OnTelemetry;
            PopulateAccounts();
            RefreshPlanAnchors();
            await LoadAsync(allowCached: true);
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            App.Services.Telemetry.SnapshotUpdated -= OnTelemetry;
            _request?.Cancel();
            InvalidatePlan();
            ScenarioText.Text = "Estimate again for the current session and quota.";
        };
    }

    private App App => (App)Application.Current;

    private void PopulateAccounts()
    {
        string? selected = SelectedAccountKey;
        string?[] keys = App.Services.Telemetry.Latest.QuotaSnapshots.Select(x => x.AccountKey)
            .Concat(App.Services.Settings.RolloutAccountAssociations.Select(x => x.AccountKey))
            .Append(selected).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Order().ToArray();
        if (Account.ItemsSource is AccountOption[] options &&
            options.Select(x => x.Key).SequenceEqual(new string?[] { null }.Concat(keys))) return;
        _populatingAccounts = true;
        Account.ItemsSource = new[] { new AccountOption(null, "All local evidence · no assumed account ownership") }
            .Concat(
                keys.Select((key, i) => new AccountOption(
                    key,
                    $"Account {i + 1} · …{key![Math.Max(0, key!.Length - 8)..]} · attributed evidence only"))).ToArray();
        Account.SelectedItem = Account.Items.Cast<AccountOption>().FirstOrDefault(x => x.Key == selected) ?? Account.Items[0];
        _populatingAccounts = false;
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async void OnAccountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _populatingAccounts) return;
        InvalidatePlan();
        RenderCurrent();
        await LoadAsync();
    }

    private void OnTelemetry(TelemetrySnapshot telemetry)
    {
        if (Interlocked.Exchange(ref _telemetryRenderQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _telemetryRenderQueued, 0);
            if (!_loaded) return;
            PopulateAccounts();
            RefreshPlanAnchors();
            if (_snapshot is { } snapshot)
            {
                CodexIntelligenceSnapshot live = App.Services.CodexIntelligence.Current;
                _snapshot = CodexIntelligenceProjection.WithLiveContext(snapshot, live, SelectedAccountKey);
                if (!snapshot.Selection.HasWorkFilter && snapshot.Selection.AccountKey is null &&
                    snapshot.Selection.FromUtc <= DateTimeOffset.UtcNow && snapshot.Selection.ToUtc >= DateTimeOffset.UtcNow.AddMinutes(-5))
                    _snapshot = _snapshot with { Workload = live.Workload };
            }
            RenderCurrent();
        })) Interlocked.Exchange(ref _telemetryRenderQueued, 0);
    }

    private void RenderCurrent()
    {
        CodexIntelligenceSnapshot latest = App.Services.CodexIntelligence.Current;
        CodexIntelligenceSnapshot snapshot = CodexIntelligenceProjection.WithLiveContext(_snapshot ?? latest, latest, SelectedAccountKey);

        static string EvenBurn(QuotaSnapshot quota)
        {
            return QuotaEvenBurn.FromSnapshot(quota) is { } pace
                ? $"\nEven burn at reading: {pace.UsedPercent:0.#}% used / {pace.ElapsedPercent:0.#}% elapsed; {Math.Abs(pace.ExcessPercentagePoints):0.#} pp {(pace.ExcessPercentagePoints >= 0 ? "above" : "below")} even burn. Inferred window start; not a forecast."
                : "\nEven burn unavailable: reset/duration missing.";
        }

        CurrentText.Text = snapshot.Current.Count == 0
            ? "No current quota reading for this account. Work history has a separate scope."
            : string.Join(
                "\n",
                snapshot.Current.Select(x =>
                    $"REPORTED {x.Current.Kind}: {x.Current.UsedPercent?.ToString("0.##") ?? "unknown"}% used · {x.State} · captured {x.Current.CapturedAtUtc.ToLocalTime():g} · reset {x.Current.ResetsAtUtc?.ToLocalTime().ToString("g") ?? "unknown"}" +
                    EvenBurn(x.Current)));
        ForecastText.Text = string.Join(
            "\n",
            snapshot.Current.Select(x =>
                $"ESTIMATED {x.Current.Kind}: {x.Forecast?.ProjectedRemainingAtResetPercent?.ToString("0.##") ?? "unavailable"}% remaining at reset · sustainable {x.Forecast?.SustainablePercentPerHour?.ToString("0.##") ?? "unknown"} pp/hour\n{x.HistoryPolicy}\n{x.Diagnostic}" +
                (x.Forecast?.Evidence?.HorizonPredictions is { } horizons
                    ? "\n" + string.Join(
                        "\n",
                        horizons.Select(h =>
                            $"At {h.TargetUtc.ToLocalTime():g}: {h.ExpectedUsagePercent:0.##} pp · {h.Model} · {h.ValidationSamples} validation samples · {h.Explanation}"))
                    : "")));
        if (snapshot.Workload is { } work)
            ForecastText.Text += "\n" + string.Join(
                "\n",
                work.Predictions.Select(x => $"LOCAL NOWCAST {x.HorizonHours * 60:g} min: {x.ExpectedTokens:N0} tokens · {x.Explanation}"));
        if (string.IsNullOrWhiteSpace(ForecastText.Text))
            ForecastText.Text = "No compatible forecast for this scope. Historical selections are not live predictions.";
        RenderDashboard();
        RenderManifest();
    }

    private void RenderManifest()
    {
        if (_snapshot is not { } snapshot) return;
        ManifestText.Text = "Selected history / analysis: " + snapshot.Manifest.Id + "\n" +
                            string.Join("\n", snapshot.Manifest.Components.Select(x => $"{x.Key}: {x.Value}")) + "\n" +
                            snapshot.Manifest.Reproducibility +
                            "\nLive forecasts refresh independently of the selected history capture:\n" + string.Join(
                                "\n",
                                snapshot.Current.Select(x =>
                                    $"{x.Current.Kind} · {x.Current.CapturedAtUtc:O} · {x.Forecast?.Evidence?.InferenceManifest?.Id ?? "artifact unavailable (not retroactively invented)"}"));
    }

    private CodexSelection ReadSelection()
    {
        static string? Optional(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
        return new(new DateTimeOffset((FromDate.Date ?? DateTimeOffset.UtcNow).Date, TimeSpan.Zero),
            new DateTimeOffset((ToDate.Date ?? DateTimeOffset.UtcNow).Date, TimeSpan.Zero),
            SelectedAccountKey, Optional(Model), Optional(Project), Optional(Thread));
    }

    public void ReleaseHistory()
    {
        if (_loaded) return;
        if (_snapshot is not null) WorkDiagnostics.Log.CacheInvalidated((int)WorkPhase.SelectedHistory, 3);
        _snapshot = null;
        _historyGeneration = null;
        _workLedger = null;
        Groups.ItemsSource = null;
        Timeline.Children.Clear();
        SummaryText.Text = ProviderText.Text = EvidenceText.Text = ManifestText.Text = TimelineText.Text =
            AccountingText.Text = AccountingMethodText.Text = PeriodsText.Text = "";
        TtValue.Text = TtBasisLabel.Text = PlanMeasuredWork.Text = ReferenceDetails.Text = ReferenceActionStatus.Text = "";
        SaveTtButton.IsEnabled = false;
    }

    private async Task LoadAsync(bool allowCached = false)
    {
        if (!_loaded) return;
        _request?.Cancel();
        var request = new CancellationTokenSource();
        _request = request;
        using var work = WorkDiagnostics.Measure(WorkPhase.SelectedHistory);
        try
        {
            CodexSelection selection = ReadSelection();
            RuntimeSettings settings = App.Services.Settings;
            SqliteReadGeneration? before = await App.Services.PresentationDataGeneration.ReadAsync(request.Token);
            if (!_loaded || _request != request || request.IsCancellationRequested) return;
            if (allowCached && before is not null && before == _historyGeneration &&
                ReferenceEquals(settings, _historySettings) && _snapshot?.Selection == selection &&
                DateTimeOffset.UtcNow - _historyLoadedAt is var age && age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(30))
            {
                HistoryCacheHits++;
                work?.Complete(rows: 0, cacheHits: 1);
                RenderCurrent();
                return;
            }
            _snapshot = null;
            RenderCurrent();
            WorkValue.Text = "Loading…";
            TtValue.Text = "Loading…";
            TtBasisLabel.Text = PlanMeasuredWork.Text = ReferenceDetails.Text = "";
            SaveTtButton.IsEnabled = false;
            WorkDetail.Text = WorkRange.Text = NowcastSummary.Text = "";
            Groups.ItemsSource = null;
            Timeline.Children.Clear();
            SummaryText.Text =
                ProviderText.Text = EvidenceText.Text = ManifestText.Text = TimelineText.Text = TimelineLegend.Text = "";
            AccountingText.Text = "Run retrospective analysis explicitly; it does not change production forecasts.";
            AccountingMethodText.Text = "";
            PeriodsText.Text = "";
            StatusText.Text = "Reading selected evidence…";
            HistoryReadCount++;
            CodexIntelligenceSnapshot snapshot = await App.Services.CodexIntelligence.QueryAsync(selection, request.Token);
            SqliteReadGeneration? after = await App.Services.PresentationDataGeneration.ReadAsync(request.Token);
            if (!_loaded || _request != request || request.IsCancellationRequested) return;
            _historyGeneration = before == after ? after : null;
            _historySettings = settings;
            _historyLoadedAt = DateTimeOffset.UtcNow;
            _snapshot = CodexIntelligenceProjection.WithLiveContext(snapshot, App.Services.CodexIntelligence.Current, selection.AccountKey);
            UpdateModelDiscoveryHint();
            StatusText.Text = $"{selection.FromUtc:yyyy-MM-dd} → {selection.ToUtc:yyyy-MM-dd} UTC · captured {snapshot.AsOfUtc.ToLocalTime():g}";
            string apiReference = snapshot.Reference?.PriceSummary ?? "API reference unavailable";
            SummaryText.Text =
                $"{snapshot.Ledger.Sum(x => x.Workload.ReportedTotalTokens):N0} observed tokens · {snapshot.Ledger.Select(x => x.ThreadId).Distinct().Count():N0} chats\n" +
                $"{apiReference} · {snapshot.Reference?.TtSummary ?? "TT unavailable"}";
            RenderReferenceDetails();
            ProviderText.Text = CodexServerCollectionScope.DescribeLatest(snapshot.ProviderEvidence) + "\n\n" + string.Join(
                "\n",
                snapshot.ProviderEvidence.GroupBy(x => (x.Surface, x.ThreadId, x.CorrelatedAccountKey, x.ClientVersion))
                    .Select(g => g.OrderByDescending(x => x.CollectedAtUtc).First()).Take(30)
                    .Select(x =>
                        $"REPORTED {x.Surface}: {x.State} · {x.ClientVersion} · {x.AccountEvidence} · collected {x.CollectedAtUtc.ToLocalTime():g}\n{x.Detail}\n{ProviderValues(x)}"));
            if (!snapshot.ProviderEvidence.Any())
                ProviderText.Text = "No compatible retained provider report. Missing reports are not zero usage.";
            PeriodsText.Text = string.Join(
                "\n\n",
                snapshot.HistoricalPeriods.Select(x =>
                    $"{x.StartUtc:yyyy-MM-dd HH:mm} → {x.EndUtc:yyyy-MM-dd HH:mm} UTC · {x.ReportedPercent?.ToString("0.####") ?? "unknown"}% of historical allowance\n" +
                    $"As of {x.DataAsOfUtc?.ToString("O") ?? "unknown"}; complete={x.AccountingComplete?.ToString() ?? "unknown"}; {x.Captures} captures / {x.Revisions} revisions\n" +
                    $"{string.Join(" · ", x.States)}\nAttributed local tokens: {x.AttributedLocalTokens:N0}; last compatible meter: {x.LastCompatibleMeterPercent?.ToString("0.##") ?? "unavailable"}%\n{string.Join("\n", x.Checks)}"));
            if (snapshot.Chats.Count > 0)
                PeriodsText.Text +=
                    "\n\nTask estimates against CURRENT full allowance (not shares of historical periods; never summed):\n" +
                    string.Join(
                        "\n",
                        snapshot.Chats.Select(x =>
                            $"{x.ThreadId} · {x.Report.DataStatus} · {x.Report.UsageSource} · weekly {x.Report.Amounts.WeeklyLimitPercent?.ToString("0.####") ?? "unknown"}% · balance credits {x.Report.Amounts.BalanceUsageCredits?.ToString() ?? "unknown"}\n{x.SelectedLocalTokens:N0} local tokens in selection · as of {x.DataAsOfUtc?.ToString("O") ?? "unknown"}\n{x.Compatibility}"));
            EvidenceText.Text = string.Join("\n\n", snapshot.EvidenceHealth.Select(x => $"{x.Source} · {x.State}\n{x.Explanation}")) +
                                $"\nAPI coverage: {snapshot.Reference?.CurrentPrice?.PricedTokens?.ToString("N0") ?? "unknown"} priced / {snapshot.Reference?.CurrentPrice?.UnpricedTokens?.ToString("N0") ?? "unknown"} unpriced tokens." +
                                (snapshot.JointWorkload is { } joint ? $"\n\n{joint.Describe()}\n" + string.Join("\n", joint.Cells.Take(16).Select(cell =>
                                    $"{cell.Coordinate!.Model ?? "unknown model"} · {cell.Coordinate.ReasoningEffort ?? "unknown effort"}: " +
                                    $"input {cell.UncachedInput:N0}, cache read {cell.CacheRead:N0}, cache write {cell.CacheWrite:N0}, " +
                                    $"output {cell.NonReasoningOutput:N0}, reasoning {cell.ReasoningOutput:N0}; reported {cell.ReportedTotal:N0}")) +
                                    (joint.Cells.Count > 16 ? "\nFirst 16 coordinate rows shown; projection retains the remaining supported rows." : "") :
                                    "\nJoint workload unavailable in this capture.") +
                                $"\n\nModel catalog: {snapshot.ModelCatalog.History.LastRead?.State.ToString() ?? "not collected"}; " +
                                $"source fetched {snapshot.ModelCatalog.History.LastRead?.SourceFetchedAtUtc?.ToString("O") ?? "unknown"}; " +
                                $"collected {snapshot.ModelCatalog.History.LastRead?.CompletedAtUtc.ToString("O") ?? "unknown"}; " +
                                $"{snapshot.ModelCatalog.History.Versions.Count} retained definitions inspected; " +
                                $"{snapshot.ModelCatalog.History.InvalidVersions} invalid; partial={snapshot.ModelCatalog.History.IsPartial}. " +
                                "Cached names, local observations and account access are separate. No model access has been verified.\n" +
                                string.Join("\n", snapshot.ModelCatalog.Choices.Take(30).Select(x => $"{x.Model} · {x.Description}" +
                                    (x.Metadata is { } m ? $" · efforts {string.Join(", ", m.ReasoningEfforts)} · source retirement {m.ReportedRetirementAtUtc?.ToString("O") ?? "unknown"} · successor {m.SuccessorModel ?? "unknown"}" : "")));
            TimelineText.Text = string.Join(
                                    "\n",
                                    snapshot.Timeline.TakeLast(100)
                                        .Select(x => $"OBSERVED {x.StartUtc:MM-dd HH:mm} UTC · {x.Tokens:N0} tokens")) + "\n" +
                                string.Join(
                                    "\n",
                                    snapshot.QuotaTimeline.TakeLast(100).Select(x =>
                                        $"REPORTED {x.CapturedAtUtc:MM-dd HH:mm} UTC · {x.Kind} {x.UsedPercent:0.##}% · {x.Source}"));
            RenderCurrent();
            RenderGroups();
            RenderTimeline();
            work?.Complete(rows: snapshot.Ledger.Count, cacheHits: 0);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (_loaded && _request == request) StatusText.Text = "Selection unavailable: " + e.GetBaseException().Message;
        }
        finally
        {
            if (_request == request) _request = null;
            request.Dispose();
        }
    }

    private void OnDimensionChanged(object sender, SelectionChangedEventArgs e)
    {
        RenderGroups();
    }

    private static string ProviderValues(CodexServerObservation row)
    {
        static string N(object? value)
        {
            return value?.ToString() ?? "unknown";
        }

        if (row.Activity is { } activity)
            return
                $"Account activity: lifetime tokens {N(activity.LifetimeTokens)}, peak day {N(activity.PeakDailyTokens)}, streak {N(activity.CurrentStreakDays)} days. Coverage/time zone not equated to local history.\n" +
                string.Join(
                    "\n",
                    (activity.DailyUsageBuckets ?? []).TakeLast(31)
                    .Select(x => $"Provider date {x.StartDate}: {x.Tokens:N0} tokens (last 31 buckets shown)"));
        if (row.DailyReport is { } daily)
            return
                $"Requested {daily.StartDate} → {daily.EndDate}; unit {N(daily.Units)}; freshness {N(daily.DataFreshness)}. Request-range-relative usage is NOT historical allowance consumption.\n" +
                string.Join(
                    "\n",
                    daily.Days.Select(x =>
                        $"{x.Date}: {N(x.TotalTokens)} provider tokens · credits {N(x.Credits)} · on-demand credits {N(x.OnDemandCredits)} · " +
                        string.Join(", ", (x.SurfaceUsage ?? new Dictionary<string, decimal>()).Select(v => $"{v.Key}={v.Value}"))));
        if (row.ThreadUsage is { } thread)
            return
                $"Thread {thread.ThreadId}: estimated usage {thread.EstimatedUsageCreditsMicros} credit-micros; USD-micros {N(thread.EstimatedUsageUsdMicros)}. Not purchased balance.\n" +
                string.Join(
                    "\n",
                    thread.Groups.Select(g =>
                        $"{N(g.Model)} / {N(g.ReasoningEffort)} / {N(g.Speed)}: {N(g.TotalTokens)} provider tokens · {g.EstimatedUsageCreditsMicros} credit-micros"));
        if (row.PlanHistory is { } plan)
            return string.Join(
                "\n",
                plan.Periods.SelectMany(p => p.Breakdowns.Select(b =>
                    $"Period {p.Id} · {b.Dimension} (alternative partition): " + string.Join(
                        ", ",
                        b.Rows.Select(v => $"{v.Key}={v.BasisPoints / 100m:0.####} pp of that period")))));
        if (row.TaskUsage is { } tasks)
            return
                $"Requested {tasks.RequestedThreadIds.Count} chats; returned {tasks.Threads.Count}. Omitted chats are unavailable, not zero.\n" +
                string.Join(
                    "\n",
                    tasks.Threads.SelectMany(t => t.Groups.Select(g =>
                        $"{t.ThreadId} · feature {N(g.ProductExperience)} / model {N(g.Model)} / effort {N(g.ReasoningEffort)} / speed {N(g.Speed)}: weekly {N(g.Amounts.WeeklyLimitPercent)}% of CURRENT full allowance, balance credits {N(g.Amounts.BalanceUsageCredits)}")));
        return "";
    }

    private void RenderGroups()
    {
        if (_snapshot is not null && Groups is not null)
            Groups.ItemsSource = _snapshot.Breakdown.Where(x => x.Dimension == Dimension.SelectedItem?.ToString())
                .Select(x => new GroupRow(x)).ToArray();
    }

    private async void OnFilterGroup(object sender, RoutedEventArgs e)
    {
        if (Groups.SelectedItem is not GroupRow row) return;
        switch (row.Source.Dimension)
        {
            case "Model": Model.Text = row.Source.Key; break;
            case "Project": Project.Text = row.Source.Key; break;
            case "Chat": Thread.Text = row.Source.Key; break;
            default:
                StatusText.Text = "This dimension is a breakdown, not an independently attributable quota filter.";
                return;
        }
        await LoadAsync();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        App.Navigate(
            typeof(CodexPage),
            Groups.SelectedItem is GroupRow { Source.Dimension: "Chat" } row ? row.Source.Key : _snapshot?.Selection.ThreadId);
    }

    private async void OnAnalyze(object sender, RoutedEventArgs e)
    {
        if (_snapshot is not { } snapshot) return;
        AnalyzeButton.IsEnabled = false;
        _request?.Cancel();
        var request = new CancellationTokenSource();
        _request = request;
        AccountingText.Text = "Analyzing frozen held-out residuals…";
        AccountingMethodText.Text = "";
        try
        {
            CodexIntelligenceSnapshot result = await App.Services.CodexIntelligence.AnalyzeAsync(snapshot, request.Token);
            if (!_loaded || !ReferenceEquals(_snapshot?.Ledger, snapshot.Ledger) || request.IsCancellationRequested) return;
            _snapshot = result with { Current = _snapshot!.Current, CurrentState = _snapshot.CurrentState, Workload = _snapshot.Workload };
            AccountingText.Text = string.Join(
                                      "\n",
                                      result.Accounting!.Scores.Where(x => x.Candidate is "total" or "categories" or "joint-categories")
                                          .Select(x =>
                                              $"{x.Cohort.Kind} · {QuotaAccountScope.Describe(x.Cohort.AccountKey)} · {x.HorizonHours * 60:g} min · {x.Candidate}: error {x.IntervalLoss?.ToString("0.###") ?? "unavailable"} pp · {x.HeldOutSamples} held-out · {x.Status}")) +
                                  "\n\n" +
                                  (result.Regime!.Candidates.Count == 0
                                      ? "Regime change: no supported candidate."
                                      : string.Join(
                                          "\n",
                                          result.Regime.Candidates.Select(x =>
                                              $"{x.BoundaryLowerUtc:O} → {x.BoundaryUpperUtc:O}: residual {x.BeforeResidual:0.##} → {x.AfterResidual:0.##} pp; relative rate {x.RelativeRate?.ToString("0.##") ?? "unknown"}×. {x.Explanation}")));
            RenderManifest();
            RenderTimeline();
            if (result.Accounting.Identification is { } identification)
                AccountingText.Text += "\n\nJoint support · diagnostic only:\n" +
                    string.Join("\n", identification.Cohorts.Select(x =>
                        $"{x.Cohort.Kind} · {x.HorizonHours * 60:g} min: {x.ExaminedRows}/{x.Observations} intervals · " +
                        $"{x.ChronologicalSupport.EvaluationBlocks} blocks / {x.ChronologicalSupport.DistinctResetCycles} reset groups\n" +
                        $"Rank: first-20 {x.FrozenFirst20Joint.NumericalRank}/{x.FrozenFirst20Joint.ActiveColumns}, retained {x.RetainedJoint.NumericalRank}/{x.RetainedJoint.ActiveColumns} · " +
                        $"later mixtures outside span {x.LaterScenarios.OutsideTrainingSpan} · missing/invalid {x.MissingJointRows}/{x.InvalidJointRows}"));
            AccountingMethodText.Text = result.Accounting.Methodology + "\n\n" + result.Regime.Methodology +
                (result.Accounting.Identification is { } detail ? "\n\n" + detail.Methodology + "\n\n" +
                    string.Join("\n", detail.Cohorts.Select(x => $"{x.Cohort.Kind} · {x.HorizonHours * 60:g} min · " +
                        $"{x.Cohort.Source} · later mixtures inside span {x.LaterScenarios.WithinTrainingSpan} · " +
                        $"zero-local-work rows {x.ZeroWorkRows}, positive displayed movement {x.ZeroWorkPositiveMovementRows}, " +
                        $"positive lower envelope {x.ZeroWorkPositiveEnvelopeRows}.")) : "");
            if (result.Accounting.ScenarioSupport is { Count: > 0 } support)
            {
                AccountingText.Text += "\n\nConditional scenario bounds · research: " +
                    string.Join(" · ", support.GroupBy(x => x.Result.Status).Select(x => $"{x.Count()} {x.Key}")) +
                    ". Cases and assumptions below.";
                AccountingMethodText.Text += "\n\n" + QuotaScenarioSupport.Methodology + "\n\n" +
                    string.Join("\n", support.Select(x => $"{x.Cohort.Kind} · {x.HorizonHours * 60:g} min · {x.Scenario} " +
                        $"({x.ScenarioObservedAtUtc:O}) · {x.AssumptionCase}: {x.Result.Status} · " +
                        $"{x.Result.LowerCost?.ToString("0.###") ?? "?"}–{x.Result.UpperCost?.ToString("0.###") ?? "unbounded/unavailable"} pp · {x.Result.Reason}"));
            }
            AccountingMethodText.Text += "\n\n" + QuotaUncertaintyDiagnosticsBuilder.Methodology + "\n\n" +
                string.Join("\n", result.Accounting.Scores.Where(x => x.Candidate is "total" or "categories" or "joint-categories")
                    .Where(x => x.Uncertainty is not null).Select(x =>
                    {
                        var u = x.Uncertainty!;
                        return $"{x.Cohort.Kind} · {x.HorizonHours * 60:g} min · {x.Candidate}: " +
                            $"point-error bounds {u.MeanMinimumAbsolutePointError?.ToString("0.###") ?? "?"}–{u.MeanMaximumAbsolutePointError?.ToString("0.###") ?? "?"} pp ({u.PointSamples} outcomes); " +
                            $"residual ranges {u.RangeSamples}, width {u.MeanPredictiveRangeWidth?.ToString("0.###") ?? "?"} pp, " +
                            $"envelope intersection {u.EnvelopeIntersectionRate?.ToString("P0") ?? "?"}, containment {u.EnvelopeContainmentRate?.ToString("P0") ?? "?"}.";
                    }));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (_loaded && _snapshot == snapshot) AccountingText.Text = "Analysis unavailable: " + error.GetBaseException().Message;
        }
        finally
        {
            if (_request == request) _request = null;
            request.Dispose();
            AnalyzeButton.IsEnabled = true;
        }
    }

    private async void OnCollectHistorical(object sender, RoutedEventArgs e)
    {
        if (!App.Services.Settings.ExperimentalCodexBackendEnabled)
        {
            StatusText.Text = "Enable the experimental read-only backend adapter in Settings first. No credentials were read.";
            return;
        }
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            StatusText.Text = "Checking provider history with account brackets; no auth refresh…";
            await App.Services.ServerEvidence.CollectHistoricalAsync(CancellationToken.None);
            if (_loaded) await LoadAsync();
        }
        catch (Exception)
        {
            if (_loaded) StatusText.Text = "Historical collection failed. Previous observations remain historical, not fresh.";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderTimeline();
    }

    private void RenderTimeline()
    {
        if (_snapshot is null || Timeline.ActualWidth <= 0) return;
        Timeline.Children.Clear();
        IReadOnlyList<CodexLedgerBucket> rows = _snapshot.Timeline;
        long maximum = rows.Select(x => x.Tokens).DefaultIfEmpty(0).Max();
        double span = (_snapshot.Selection.ToUtc - _snapshot.Selection.FromUtc).TotalSeconds;
        double width = Math.Max(1, Timeline.ActualWidth - 8);

        double X(DateTimeOffset at)
        {
            return Math.Clamp((at - _snapshot.Selection.FromUtc).TotalSeconds / span, 0, 1) * width;
        }

        foreach (CodexLedgerBucket row in rows)
        {
            double height = maximum > 0 ? 90.0 * row.Tokens / maximum : 0;
            double barWidth = Math.Max(1, Math.Min(64, width / Math.Max(1, rows.Count) * .7));
            var bar = new Rectangle
            {
                Width = barWidth,
                Height = height,
                Fill = new SolidColorBrush(Colors.SteelBlue),
                RadiusX = 2,
                RadiusY = 2,
            };
            Canvas.SetLeft(bar, Math.Min(width - barWidth, X(row.StartUtc)));
            Canvas.SetTop(bar, 94 - height);
            Timeline.Children.Add(bar);
            ToolTipService.SetToolTip(bar, $"{row.StartUtc:dd MMM HH:mm} UTC · {row.Tokens:N0} observed tokens");
        }
        // A separate band and one compatible lane: never superimpose token and quota scales,
        // connect sources, or interpret a reset drop as negative consumption.
        QuotaSnapshot? anchor = _snapshot.QuotaTimeline.Where(x => x.Authority == QuotaObservationAuthority.ProviderAuthoritative)
                                    .MaxBy(x => x.CapturedAtUtc);
        QuotaSnapshot[] quota = anchor is null
            ? []
            : _snapshot.QuotaTimeline.Where(x =>
                QuotaHistoryPolicy.Cohort(x) == QuotaHistoryPolicy.Cohort(anchor) &&
                x.UsedPercent is >= 0 and <= 100).TakeLast(1000).ToArray();
        foreach (QuotaSnapshot row in quota)
        {
            var dot = new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(Colors.DarkOrange) };
            Canvas.SetLeft(dot, X(row.CapturedAtUtc));
            Canvas.SetTop(dot, 151 - row.UsedPercent!.Value * .4);
            Timeline.Children.Add(dot);
            ToolTipService.SetToolTip(dot, $"{row.Kind} · {row.UsedPercent:0.##}% used · {row.CapturedAtUtc:dd MMM HH:mm} UTC");
        }
        var first = new TextBlock { Text = _snapshot.Selection.FromUtc.ToString("dd MMM"), FontSize = 12 };
        Canvas.SetTop(first, 160);
        Timeline.Children.Add(first);
        var last = new TextBlock { Text = _snapshot.Selection.ToUtc.AddTicks(-1).ToString("dd MMM"), FontSize = 12 };
        Canvas.SetTop(last, 160);
        Canvas.SetLeft(last, Math.Max(0, width - 55));
        Timeline.Children.Add(last);
        TimelineLegend.Text = $"Blue · local tokens, 0–{Compact(maximum)} per bucket. " +
                              (quota.Length == 0
                                  ? "No compatible quota history for this selection."
                                  : $"Orange · {anchor!.Kind} quota used, 0–100%, separate lower band. Same dates; not attributed token cost.");
    }

    private sealed record AccountOption(string? Key, string Label)
    {
        public override string ToString()
        {
            return Label;
        }
    }

    private sealed record GroupRow(CodexLedgerGroup Source)
    {
        public string Label => Source.Key;
        public string Detail => $"{Source.Tokens:N0} observed tokens · {Source.Threads:N0} chats";

        public override string ToString()
        {
            return Label;
        }
    }
}
