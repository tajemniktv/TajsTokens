using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class SessionPlanProjectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
    private static readonly QuotaSnapshot Anchor = new(QuotaWindowKind.FiveHour, Now, 70, 300,
        Now.AddHours(2), "codex", "default", "codex-app-server", "account");
    private static CurrentQuotaForecast Reading => new(Anchor, TelemetryHealthState.Live, null, "");
    private static ScenarioWindowEstimate Estimate => new(Anchor.Kind, true, 20, 15, null, null, 0, "baseline")
        { Cohort = QuotaHistoryPolicy.Cohort(Anchor) };

    [Fact]
    public void PointEstimateWithoutBandAnswersFitConditionally()
    {
        SessionPlanWindow result = SessionPlanProjection.Build(Estimate, Reading, 1, Now);
        Assert.Equal(SessionFit.Fits, result.Fit);
        Assert.Contains("15% left", result.Detail);
        Assert.Contains("Conditional point", result.Detail);
        Assert.Equal(SessionFit.Exceeds, SessionPlanProjection.Build(Estimate with { ExpectedQuotaDeltaPercent = 40 }, Reading, 1, Now).Fit);
    }

    [Fact]
    public void BandSpanningAllowanceIsUncertainAndResetCrossingDoesNotClaimFit()
    {
        Assert.Equal(SessionFit.Uncertain, SessionPlanProjection.Build(Estimate with
            { LowerQuotaDeltaPercent = 10, UpperQuotaDeltaPercent = 40 }, Reading, 1, Now).Fit);
        SessionPlanWindow crossing = SessionPlanProjection.Build(Estimate, Reading, 3, Now);
        Assert.Equal(SessionFit.Unknown, crossing.Fit);
        Assert.Contains("Reset occurs during", crossing.Detail);
        Assert.Contains("15 pp expected consumption", crossing.Detail);
        Assert.DoesNotContain("left after", crossing.Detail);
    }

    [Fact]
    public void StaleMissingAndChangedCohortCannotBorrowAnEstimate()
    {
        Assert.Equal(SessionFit.Unknown, SessionPlanProjection.Build(Estimate, null, 1, Now).Fit);
        Assert.Equal(SessionFit.Unknown, SessionPlanProjection.Build(Estimate, Reading with { State = TelemetryHealthState.Stale }, 1, Now).Fit);
        foreach (QuotaSnapshot changed in new[] { Anchor with { AccountKey = "other" }, Anchor with { Source = "other" },
                     Anchor with { PlanType = "new" }, Anchor with { LimitId = "other" }, Anchor with { WindowMinutes = 600 } })
            Assert.Equal(SessionFit.Unknown, SessionPlanProjection.Build(Estimate, Reading with { Current = changed }, 1, Now).Fit);
    }

    [Fact]
    public void EditsQuotaChangesAndUnloadInvalidateBothResultsAndAutofill()
    {
        var revision = new PlannerRevision();
        long autofill = revision.Value;
        revision.Invalidate();
        Assert.False(revision.IsCurrent(autofill));
        long estimate = revision.Value;
        revision.Invalidate();
        Assert.False(revision.IsCurrent(estimate));
        Assert.True(revision.IsCurrent(revision.Value));
    }

    [Fact]
    public void ScenariosHonorExplicitLiveCohortsWithoutBorrowingOlderPlans()
    {
        QuotaHistoryCohort cohort = QuotaHistoryPolicy.Cohort(Anchor);
        ScenarioHistorySample[] samples = Enumerable.Range(1, 24).Select(i => new ScenarioHistorySample(
            Anchor.Kind, Now.AddHours(-i - 1), Now.AddHours(-i), 2, 1, 0, "model", "high",
            Now.AddDays(1), Anchor.Source, Anchor.AccountKey, cohort)).ToArray();
        ScenarioRequest request = new(1, 1, 0, AccountKey: Anchor.AccountKey) { Cohorts = [cohort with { PlanType = "new" }] };
        Assert.False(QuotaPredictionService.Simulate(request, samples, Now).FiveHour.HasEnoughHistory);
        ScenarioEstimate supported = QuotaPredictionService.Simulate(request with { Cohorts = [cohort] }, samples, Now);
        Assert.True(supported.FiveHour.HasEnoughHistory);
        Assert.Equal(cohort, supported.FiveHour.Cohort);
        Assert.Equal(Now, supported.EvaluatedAtUtc);
        Assert.False(supported.Weekly.HasEnoughHistory);
    }
}
