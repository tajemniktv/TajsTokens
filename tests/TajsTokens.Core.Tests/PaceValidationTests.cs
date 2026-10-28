using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;

namespace TajsTokens.Core.Tests;

public sealed class PaceValidationTests
{
    [Fact]
    public void TargetToleranceAvailabilityAndSupportPartitionsAreExplicitAndFutureWorkCannotChangeResults()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        QuotaSnapshot[] quota = Enumerable.Range(0, 80).Select(i => new QuotaSnapshot(QuotaWindowKind.Weekly,
            start.AddMinutes(i * 15), 10 + i * .5, 10080, start.AddDays(7), "codex", "default", "codex-app-server:private-source", "private-account")
        { CollectedAtUtc = start.AddMinutes(i * 15), HasSourceTimestamp = true }).ToArray();
        var data = new CodexForecastDataset(quota, [], [], [], start.AddDays(1), "private-coverage");
        PaceValidationReport report = ForecastThresholdStudy.ValidatePace(data);
        Assert.NotEmpty(report.Scores);
        Assert.All(report.Scores, x =>
        {
            Assert.InRange(x.PointOrigins, 0, x.CandidateOrigins);
            Assert.InRange(x.PairedOutcomes, 0, x.PointOrigins);
            Assert.InRange(x.NonOverlappingOutcomes, 0, x.PairedOutcomes);
            Assert.InRange(x.BandedOutcomes, 0, x.PairedOutcomes);
            Assert.InRange(x.MissedObservedMovement, 0, x.PairedOutcomes);
        });
        Assert.Contains(report.Scores, x => x.Target == "0.08333333333333333h" && x.PointOrigins > 0 && x.PairedOutcomes == 0);
        Assert.Contains(report.Scores, x => x.Target == "0.25h" && x.PairedOutcomes > 0);
        Assert.Contains(report.Scores, x => x.Target == "near-reset-proxy" && x.PointOrigins > 0 && x.PairedOutcomes == 0);
        Assert.Contains(report.Scores, x => x.Model == "persistence" && x.MissedObservedMovement > 0);
        foreach (var overall in report.Scores.Where(x => x.Partition == "overall"))
            Assert.Equal(overall.CandidateOrigins, report.Scores.Where(x => x.Partition == "state-reset-lead" &&
                x.Cohort == overall.Cohort && x.Target == overall.Target && x.Period == overall.Period && x.Model == overall.Model).Sum(x => x.CandidateOrigins));
        string json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("private-", json);
        var future = data with { Tokens = [new("private-session", start.AddDays(2), start.AddDays(2), "future-model", null, 100, 0, 0, 0, 0, 100)] };
        Assert.Equal(report.Scores, ForecastThresholdStudy.ValidatePace(future).Scores);
        Assert.All(report.Scores.Where(x => x.Partition == "state-reset-lead"), x => Assert.Equal("missing-local-evidence", x.LocalState));
    }
}
