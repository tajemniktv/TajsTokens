using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class RecentWorkPlanTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T12:00:00Z");
    private static CodexPredictiveTokenEvent Row(string? model = "A", string? effort = "high", long input = 1000, long output = 500) =>
        new("private-session", Now.AddMinutes(-10), Now.AddMinutes(-9), model, effort, input, 0, 0, output, 0, input + output);
    private static CodexForecastDataset Data(params CodexPredictiveTokenEvent[] rows) => new([], [], rows, [], Now, "fixture");
    private static QuotaSnapshot Anchor(QuotaWindowKind kind = QuotaWindowKind.Weekly, string account = "account") =>
        new(kind, Now, 60, kind == QuotaWindowKind.Weekly ? 10080 : 300, Now.AddHours(4), "codex", "default", "codex-app-server", account)
        { LimitId = "limit", PlanType = "plan", CollectedAtUtc = Now, ObservationId = "anchor" };
    private static CurrentQuotaForecast Reading(QuotaSnapshot? anchor = null)
    {
        anchor ??= Anchor();
        var receipt = QuotaPaceModels.Receipt([anchor with { CapturedAtUtc = Now.AddHours(-1), UsedPercent = 50 }, anchor],
            anchor, Now, QuotaPaceModels.ProductionFallback, "fixture", true);
        var evidence = new ForecastEvidence("policy", "time-ewma-2h", anchor.Source, 2, 1, 0, null, null, null, null, "conditional")
            { PaceReceipt = receipt };
        return new(anchor, TelemetryHealthState.Live, new Forecast(anchor.Kind, Now, 10, null, true, 10, 0, ForecastState.SafeUntilReset,
            Evidence: evidence), "fixture");
    }
    private static CodexIntelligenceSnapshot Live(params CurrentQuotaForecast[] readings) => CodexIntelligenceProjection.Live(
        TelemetrySnapshot.Empty with { CapturedAtUtc = Now, QuotaDataFresh = true,
            QuotaSnapshots = readings.Select(x => x.Current).ToArray(), CurrentForecasts = readings });
    private static WorkloadReferenceState References()
    {
        var weights = new[] { "A", "B" }.SelectMany(model => Enum.GetValues<TtTokenCategory>().Select(category =>
            new TtJointWeight(new(model, null), category, model == "A" ? 1 : 2))).ToArray();
        var basis = new TtJointWorkloadBasis(TtJointWorkloadBasis.Version, TtJointDimensions.Model, "same-token fixture", "relative", "fixture",
            ["fixture"], weights, [new(new("A", null), TtTokenCategory.UncachedInput, 1000)]);
        return new(new(Now.AddDays(-1), "official", basis), null);
    }
    private static RecentWorkPlan Build(CodexForecastDataset? data = null, RecentWorkPlanRequest? request = null,
        CodexIntelligenceSnapshot? live = null) => RecentWorkPlanBuilder.Build(data ?? Data(Row()), live ?? Live(Reading()),
            request ?? new(1), References(), Now);

    [Fact]
    public void RecentWorkUsesElapsedJointContextAndKeepsAccountPaceSeparate()
    {
        var result = Build();
        Assert.Equal(750, result.Continuation.Workload!.ExpectedTokens);
        Assert.Equal(.75m, result.Reference!.Tt!.ExactTt);
        Assert.Equal(CodexJointQuantityKind.ConditionalProjection, result.Reference.Tt.Kind);
        var weekly = result.Windows.Single(x => x.Kind == QuotaWindowKind.Weekly);
        Assert.Equal(10, weekly.Pace.ExpectedQuotaDeltaPercent);
        Assert.Equal(SessionFit.Fits, weekly.Assessment.Fit);
        Assert.Null(weekly.Pace.LowerQuotaDeltaPercent);
        Assert.Contains("Separate from local work", weekly.Pace.Explanation);
        var largeLocal = Build(Data(Row(input: 1000000, output: 0)));
        Assert.NotEqual(result.Reference.Tt.ExactTt, largeLocal.Reference!.Tt!.ExactTt);
        Assert.Equal(weekly.Pace, largeLocal.Windows.Single(x => x.Kind == weekly.Kind).Pace);
        var shortContext = Build(request: new(1, .5));
        Assert.Equal(3000, shortContext.Continuation.Workload!.ExpectedTokens);
        Assert.Equal(weekly.Pace, shortContext.Windows.Single(x => x.Kind == weekly.Kind).Pace);
    }

    [Fact]
    public void ChangedModelRevaluesSameJointAmountsWithoutTaskOrQuotaPrediction()
    {
        var data = Data(Row(), Row(model: "B", input: 2000, output: 0));
        var before = CodexJointWorkloadBuilder.Build(data.Tokens);
        var result = Build(data, new(1, Model: " B ", ReasoningEffort: "low"));
        Assert.True(result.HasEdits);
        Assert.Equal("B", result.Request.Model);
        Assert.Equal(2.75m, result.Reference!.Tt!.ExactTt);
        Assert.Equal(3.5m, result.Revaluation!.Tt!.ExactTt);
        Assert.Equal(result.Reference.Tt.TotalTokens, result.Revaluation.Tt.TotalTokens);
        Assert.Equal(before, CodexJointWorkloadBuilder.Build(data.Tokens));
        Assert.All(result.Windows, x => Assert.Null(x.Pace.ExpectedQuotaDeltaPercent));
        Assert.Contains("unsupported", result.EditStatus);
    }

    [Fact]
    public void EffortEditDoesNotInventAnExtraTariffMultiplierAndMatchingLabelsAreNotEdits()
    {
        var same = Build(request: new(1, Model: "A", ReasoningEffort: "high"));
        Assert.False(same.HasEdits); Assert.Null(same.Revaluation);
        var effort = Build(request: new(1, ReasoningEffort: "low"));
        Assert.True(effort.HasEdits);
        Assert.Equal(effort.Reference!.Tt!.ExactTt, effort.Revaluation!.Tt!.ExactTt);
        Assert.Null(effort.Windows.Single(x => x.Kind == QuotaWindowKind.Weekly).Pace.ExpectedQuotaDeltaPercent);
        var unknown = Build(request: new(1, Model: "unlisted"));
        Assert.Equal(TtScoreState.Partial, unknown.Revaluation!.Tt!.State);
        Assert.Null(unknown.Revaluation.Tt.ExactTt);
    }

    [Fact]
    public void MissingAndLateLocalWorkDoNotHideIndependentQuotaOrBecomeZeroWork()
    {
        foreach (var data in new[] { Data(), Data(Row() with { CapturedAtUtc = null }),
                     Data(Row() with { CapturedAtUtc = Now.AddMinutes(1) }), Data(Row() with { ObservedAtUtc = Now.AddMinutes(1) }) })
        {
            var result = Build(data);
            Assert.Null(result.Continuation.Receipt.ProjectedTokens); Assert.Null(result.Reference);
            Assert.True(result.Windows.Single(x => x.Kind == QuotaWindowKind.Weekly).Pace.HasEnoughHistory);
        }
        var zero = Build(Data(Row(input: 0, output: 0)));
        Assert.Equal(0, zero.Continuation.Receipt.ProjectedTokens);
        Assert.Equal(0, zero.Reference!.Tt!.ExactTt);
        var inconsistent = Build(Data(Row() with { ReportedTotalTokens = 9999 }));
        Assert.NotNull(inconsistent.Continuation.Receipt.ProjectedTokens);
        Assert.Null(inconsistent.Reference);
    }

    [Fact]
    public void AccountSelectionDoesNotFilterLocalWorkAndNeverBorrowsAnotherMeter()
    {
        var first = Reading(); var second = Reading(Anchor(account: "other"));
        var all = Build(live: Live(first, second));
        Assert.All(all.Windows, x => Assert.False(x.Pace.HasEnoughHistory));
        var selected = Build(request: new(1, AccountKey: "other"), live: Live(first, second));
        Assert.Equal(all.Reference!.Tt!.ExactTt, selected.Reference!.Tt!.ExactTt);
        Assert.Equal("other", selected.Windows.Single(x => x.Kind == QuotaWindowKind.Weekly).Pace.Cohort!.AccountKey);
        var absent = Build(request: new(1, AccountKey: "missing"), live: Live(first, second));
        Assert.Equal(all.Reference.Tt.ExactTt, absent.Reference!.Tt!.ExactTt);
        Assert.All(absent.Windows, x => Assert.False(x.Pace.HasEnoughHistory));
    }

    [Fact]
    public void ResetCrossingRetainsConditionalAmountsWithoutClaimingFitOrNewCapacity()
    {
        var result = Build(request: new(5));
        Assert.Equal(3750, result.Continuation.Workload!.ExpectedTokens);
        var weekly = result.Windows.Single(x => x.Kind == QuotaWindowKind.Weekly);
        Assert.Equal(50, weekly.Pace.ExpectedQuotaDeltaPercent);
        Assert.Equal(SessionFit.Unknown, weekly.Assessment.Fit);
        Assert.Contains("Reset occurs during", weekly.Assessment.Detail);
        Assert.DoesNotContain("left after", weekly.Assessment.Detail);
    }

    [Fact]
    public void StaleFlatIncompatibleAndFuturePaceAreWithheldWithoutHidingLocalAmounts()
    {
        var reading = Reading(); var forecast = reading.Forecast!;
        var receipt = forecast.Evidence!.PaceReceipt!;
        foreach (var invalid in new[] { reading with { State = TelemetryHealthState.Stale },
                     reading with { Current = reading.Current with { CapturedAtUtc = Now.AddHours(-7) } },
                     reading with { Forecast = forecast with { IsQuantizedFlat = true } },
                     reading with { Forecast = forecast with { GeneratedAtUtc = Now.AddSeconds(1) } },
                     reading with { Forecast = forecast with { Evidence = forecast.Evidence with { PaceReceipt = receipt with { Cohort = receipt.Cohort with { PlanType = "other" } } } } },
                     reading with { Forecast = forecast with { Evidence = forecast.Evidence with { PaceReceipt = receipt with { RatePercentPerHour = 0 } } } } })
        {
            var result = Build(live: Live(invalid));
            Assert.NotNull(result.Reference!.Tt!.ExactTt);
            Assert.False(result.Windows.Single(x => x.Kind == QuotaWindowKind.Weekly).Pace.HasEnoughHistory);
        }
    }

    [Fact]
    public void ActualProductionReceiptFeedsThePlanAndAbsentWindowRemainsIndependent()
    {
        var anchor = Anchor();
        var history = Enumerable.Range(0, 5).Select(i => anchor with
            { CapturedAtUtc = Now.AddMinutes(-120 + i * 30), CollectedAtUtc = Now.AddMinutes(-120 + i * 30), UsedPercent = 40 + i * 5 }).ToArray();
        var forecast = QuotaPredictionService.BuildResetOutlook(history, Now);
        var current = new CurrentQuotaForecast(anchor, TelemetryHealthState.Live, forecast, "production");
        var live = Live(current);
        live = live with { CurrentState = live.CurrentState with { QuotaLanes = [
            new(QuotaWindowKind.Weekly, "codex", "default", anchor, TelemetryHealthState.Live),
            new(QuotaWindowKind.FiveHour, "codex", "default", null, TelemetryHealthState.Live, NotReportedByProvider: true)] } };
        var result = Build(live: live);
        var weekly = result.Windows.Single(x => x.Kind == QuotaWindowKind.Weekly);
        Assert.True(weekly.Pace.HasEnoughHistory);
        Assert.Equal(10, weekly.Pace.ExpectedQuotaDeltaPercent!.Value, 8);
        Assert.Equal("Not reported", result.Windows.Single(x => x.Kind == QuotaWindowKind.FiveHour).Assessment.Summary);
        Assert.Equal(SessionFit.Fits, weekly.Assessment.Fit);
        var stale = Build(live: live with { CurrentState = live.CurrentState with { QuotaLanes = live.CurrentState.QuotaLanes.Select(x =>
            x.Kind == QuotaWindowKind.Weekly ? x with { State = TelemetryHealthState.Stale } : x).ToArray() } });
        Assert.False(stale.Windows.Single(x => x.Kind == QuotaWindowKind.Weekly).Pace.HasEnoughHistory);
    }

    [Fact]
    public void MissingLabelsRemainPartialAndOverflowIsNotRelabelledForAnEdit()
    {
        var missing = Build(Data(Row(model: null)));
        Assert.Equal(TtScoreState.Partial, missing.Reference!.Tt!.State);
        Assert.Null(missing.Reference.Tt.ExactTt);
        var rows = Enumerable.Range(0, CodexJointWorkload.MaximumCoordinates + 1).Select(i => Row(model: $"model-{i}")).ToArray();
        var overflow = Build(Data(rows), new(1, Model: "B"));
        Assert.NotNull(overflow.Continuation.Workload!.JointWorkload!.Overflow);
        Assert.Null(overflow.Revaluation);
        Assert.True(overflow.HasEdits);
        Assert.All(overflow.Windows, x => Assert.False(x.Pace.HasEnoughHistory));
    }

    [Fact]
    public void FractionalHorizonPreservesExactlySummableConditionalQuantitiesForReferenceScoring()
    {
        // Category totals from a retained native frame, with fixture model/effort labels.
        var row = new CodexPredictiveTokenEvent("fixture", Now.AddMinutes(-10), Now.AddMinutes(-9), "A", "high",
            213832, 8867712, 0, 25998, 20188, 9127730);
        var result = Build(Data(row), new(5d / 60, .5));
        var joint = result.Continuation.Workload!.JointWorkload!;
        Assert.Equal(TtScoreState.Exact, result.Reference!.Tt!.State);
        Assert.Equal(joint.ReportedTokens, joint.CategoryTokens);
        Assert.InRange(Math.Abs(joint.ReportedTokens - 9127730m / 6), 0, .00000001m);
        Assert.InRange(Math.Abs(result.Reference.Tt.ExactTt!.Value - 9127730m / 6000), 0, .00000000001m);
        Assert.Equal(9127730, row.ReportedTotalTokens);
        Assert.Equal(CodexJointQuantityKind.ConditionalProjection, joint.Kind);
    }

    [Fact]
    public void ExtremeSupportedNativeMagnitudesReduceFractionDigitsWithoutWithholdingTheReference()
    {
        var result = Build(Data(Row(input: long.MaxValue / 2), Row(model: "B", input: long.MaxValue / 3)), new(168, .5));
        Assert.Equal(TtScoreState.Exact, result.Reference!.Tt!.State);
        Assert.True(result.Continuation.Workload!.JointWorkload!.IsComplete);
    }

    [Fact]
    public async Task ProductBoundaryReadsLocalLedgerWithoutAccountOrHistoryFilterAndHandlesMissingReferences()
    {
        var actualNow = DateTimeOffset.UtcNow;
        var row = Row() with { ObservedAtUtc = actualNow.AddMinutes(-1), CapturedAtUtc = actualNow.AddMinutes(-1) };
        var reader = new LedgerReader(Data() with { Ledger = [new("observation", "source", "private-session", "project", row.ObservedAtUtc,
            row.CapturedAtUtc, null, AccountEvidenceClass.Unattributed, row)] });
        var engine = new CodexIntelligenceEngine(() => reader, null!, null!, () => TelemetrySnapshot.Empty,
            workloadReferences: _ => throw new IOException("reference fixture"));
        var result = await engine.PlanRecentWorkAsync(new(1, .5, AccountKey: "selected"), default);
        Assert.Equal(3000, result.Continuation.Workload!.ExpectedTokens);
        Assert.Null(result.Reference!.Tt);
        Assert.Equal("reference-selection-unavailable", result.Reference.Sources.Issue);
        Assert.Null(reader.Account); Assert.False(reader.IncludeQuota);
        Assert.InRange((reader.To - reader.From).TotalMinutes, 30, 30.00001);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.PlanRecentWorkAsync(new(1), cancelled.Token));
    }

    [Theory]
    [InlineData(0, 2)] [InlineData(169, 2)] [InlineData(1, 1)] [InlineData(double.NaN, 2)]
    public void UnsupportedDurationOrContextIsNotSilentlyClamped(double hours, double context) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(request: new(hours, context)));

    private sealed class LedgerReader(CodexForecastDataset data) : ICodexForecastDatasetReader
    {
        public string? Account; public bool IncludeQuota; public DateTimeOffset From, To;
        public Task<CodexForecastDataset> ReadAsync(string provider, string profile, DateTimeOffset fromUtc, DateTimeOffset toUtc,
            CancellationToken cancellationToken, string? accountKey = null, bool includeQuota = true, bool includeLedger = false)
        {
            Account = accountKey; IncludeQuota = includeQuota; From = fromUtc; To = toUtc;
            Assert.True(includeLedger);
            return Task.FromResult(data);
        }
    }
}
