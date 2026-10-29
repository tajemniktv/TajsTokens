using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaCensoringStudyTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    private static CodexForecastDataset Data() => new(
        new[] { 0, 15, 45, 75, 90, 91, 92, 93, 94, 95, 125, 130, 135 }.Select(m => new QuotaSnapshot(
            QuotaWindowKind.Weekly, Start.AddMinutes(m), Math.Min(100, m), 10080, Start.AddDays(7), "codex", "default",
            "codex-app-server:private-source", "private-account")
            { HasSourceTimestamp = true, CollectedAtUtc = Start.AddMinutes(m), PlanType = "pro", LimitId = "codex" }).ToArray(), [],
        Enumerable.Range(0, 28).Select(i => new CodexPredictiveTokenEvent("private-session", Start.AddMinutes(i * 5),
            Start.AddMinutes(i * 5), "model", "high", 1000, 0, 0, 0, 0, 1000)).ToArray(), [], Start.AddHours(3), "Synthetic ceiling frame");
    private static QuotaCensoringReport Run(CodexForecastDataset data, ForecastReplayAvailability mode = ForecastReplayAvailability.ReconstructedEventTime) =>
        QuotaCensoringStudy.Evaluate(data, mode, includeAttempts: true);

    [Fact]
    public void ObserverPreservesExactOrdinaryOutputCoverageAndInputEvidence()
    {
        var data = Data(); string before = JsonSerializer.Serialize(data); var seen = new List<QuotaCostCandidate>();
        var ordinary = QuotaCostObservationBuilder.BuildDetailed(data, horizons: [.5]);
        var audit = QuotaCostObservationBuilder.BuildDetailed(data, horizons: [.5], auditCandidate: seen.Add);
        Assert.Equal(JsonSerializer.Serialize(ordinary), JsonSerializer.Serialize(audit));
        Assert.Equal(before, JsonSerializer.Serialize(data));
        Assert.Equal(8, seen.Count); Assert.Equal(6, seen.Count(a => a.EndUsed == 100));
        Assert.All(audit.Observations, a => Assert.True(a.EndUsed < 100));
        Assert.Equal(3, data.Quota.Count(q => q.UsedPercent == 100));
    }

    [Fact]
    public void OverlappingCeilingAttemptsAndSharedReportsKeepSeparateDenominators()
    {
        var report = Run(Data()); var group = Assert.Single(report.Groups, g => g.HorizonHours == .5);
        var ordinary = group.Selection.Single(s => s.Outcome == "ordinary-selected");
        var ceiling = group.Selection.Single(s => s.Outcome == "ceiling-excluded");
        Assert.Equal(11, group.CandidateStarts); Assert.Equal(2, group.OrdinaryIntervals);
        Assert.Equal(6, group.RejectedStarts["saturated-outcome"]); Assert.Equal(2, group.RejectedStarts["saturated-origin"]);
        Assert.Equal(1, group.RejectedStarts["outcome-beyond-poll-tolerance"]);
        Assert.Equal(group.CandidateStarts, group.OrdinaryIntervals + group.RejectedStarts.Values.Sum());
        Assert.Equal(6, ceiling.Attempts); Assert.Equal(1, ceiling.DistinctOutcomes); Assert.Equal(1, ceiling.NonOverlappingAttempts);
        Assert.Equal(2, ordinary.NonOverlappingAttempts); Assert.Equal(70, ordinary.MeanOriginHeadroom);
        Assert.Equal(7.5, ceiling.MeanOriginHeadroom); Assert.Equal(7.5, ceiling.MeanDisplayedMovement);
        Assert.Equal(30, ordinary.MeanDisplayedMovement); Assert.Equal(41000d / 6, ceiling.MeanRecordedTokens);
        Assert.Equal(6000, ordinary.MeanRecordedTokens); Assert.Equal(6, ceiling.TimelyAvailability);
    }

    [Fact]
    public void DelayedUnknownAndInvalidCollectionNeverBecomeTimelyCostEvidence()
    {
        var data = Data();
        foreach (DateTimeOffset? captured in new DateTimeOffset?[] { null, data.CapturedAtUtc.AddDays(1), Start.AddDays(-1) })
        {
            var altered = data with { Tokens = data.Tokens.Select(t => t with { CapturedAtUtc = captured }).ToArray() };
            var report = Run(altered);
            var ceiling = report.Groups.Single(g => g.HorizonHours == .5).Selection.Single(s => s.Outcome == "ceiling-excluded");
            Assert.Equal(6, ceiling.Attempts); Assert.Equal(0, ceiling.TimelyAvailability);
            if (captured is null) Assert.Equal(0, ceiling.KnownAvailability);
            if (captured < Start) Assert.Equal(6, ceiling.InvalidOrUnknownClocks);
        }
    }

    [Fact]
    public void CollectionReplayRetainsOriginalReportedTimesAndDoesNotBackdateInputs()
    {
        var data = Data(); var delayed = data with { Quota = data.Quota.Select(q => q with { CollectedAtUtc = q.CapturedAtUtc.AddSeconds(30) }).ToArray() };
        var report = Run(delayed, ForecastReplayAvailability.CollectedByOrigin);
        Assert.NotEmpty(report.Attempts);
        Assert.All(report.Attempts, a =>
        {
            Assert.Equal(a.OriginUtc.AddSeconds(-30), a.OriginalReportedOriginUtc);
            Assert.Equal(a.OutcomeUtc.AddSeconds(-30), a.OriginalReportedOutcomeUtc);
            Assert.True(a.NominalTargetUtc <= a.OutcomeUtc); Assert.True(a.ValidCollectionClocks);
        });
        var tooLate = data with { Quota = data.Quota.Select(q => q with { CollectedAtUtc = q.CapturedAtUtc.AddMinutes(6) }).ToArray() };
        Assert.Empty(Run(tooLate, ForecastReplayAvailability.CollectedByOrigin).Attempts);
        Assert.NotEmpty(Run(tooLate).Attempts);
    }

    [Fact]
    public void ResetAndReturningPlanBoundariesCannotBecomeCensoredTargets()
    {
        var data = Data(); var boundary = data.Quota[0] with { CapturedAtUtc = Start.AddMinutes(110), CollectedAtUtc = Start.AddMinutes(110), UsedPercent = 98, PlanType = "other" };
        var changed = Run(data with { Quota = data.Quota.Append(boundary).ToArray() });
        Assert.DoesNotContain(changed.Attempts, a => a.EndUsed >= 100);
        var reset = Run(data with { Quota = data.Quota.Select(q => q.CapturedAtUtc >= Start.AddMinutes(125)
            ? q with { ResetsAtUtc = Start.AddDays(14) } : q).ToArray() });
        Assert.DoesNotContain(reset.Attempts, a => a.EndUsed >= 100);
        Assert.Equal(3, reset.FrameCeilingReadings);
    }

    [Fact]
    public void MissingLocalWorkIsRecordedAsMissingEventsWithoutInventingAccountIdleness()
    {
        var report = Run(Data() with { Tokens = [] });
        var ceiling = report.Groups.Single(g => g.HorizonHours == .5).Selection.Single(s => s.Outcome == "ceiling-excluded");
        Assert.Equal(6, ceiling.NoRecordedTokenEvents); Assert.Equal(0, ceiling.MeanRecordedTokens);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("private-account", json); Assert.DoesNotContain("private-session", json); Assert.DoesNotContain("private-source", json);
        Assert.All(report.Groups, g => Assert.True(g.KnownAccount));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => QuotaCensoringStudy.Evaluate(Data(), ForecastReplayAvailability.ReconstructedEventTime, cancelled.Token));
        Assert.Throws<ArgumentException>(() => QuotaCensoringStudy.Evaluate(Data(), (ForecastReplayAvailability)99));
    }
}
