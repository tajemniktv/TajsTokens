using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class UsualActivityEvaluationTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    private static CodexForecastDataset History(int hours = 72) => new([], [],
        Enumerable.Range(0, hours * 12 + 1).Where(i => i % 36 < 24).Select(i => new CodexPredictiveTokenEvent(
            "session", Start.AddMinutes(i * 5), Start.AddMinutes(i * 5), "model", null,
            (i + 1) * 10, 0, 0, 0, 0, (i + 1) * 10)).ToArray(), [], Start.AddHours(hours), "Synthetic activity");
    private static UsualActivityReport Run(CodexForecastDataset data, DateTimeOffset? through = null) =>
        UsualActivityEvaluation.Evaluate(data, Start, through ?? data.CapturedAtUtc, includeOrigins: true);

    [Fact]
    public void AllFourHorizonsKeepFixedSplitMatchedTargetsAndQuietLabelsSeparate()
    {
        var data = History(); string before = JsonSerializer.Serialize(data);
        var report = Run(data);
        Assert.Equal(48, report.Scores.Count); Assert.Equal(8, report.Coverage.Count);
        Assert.Equal(Start.AddHours(72 * .8), report.SplitUtc);
        Assert.Contains(report.Origins, t => t.ObservedTokens == 0);
        foreach (double h in UsualActivityEvaluation.Horizons)
        {
            var rows = report.Origins.Where(t => t.HorizonHours == h).OrderBy(t => t.OriginUtc).ToArray();
            Assert.NotEmpty(rows); Assert.All(rows, t => Assert.Equal(TimeSpan.FromHours(h), t.EndUtc - t.OriginUtc));
            for (int i = 1; i < rows.Length; i++) Assert.True(rows[i - 1].EndUtc <= rows[i].OriginUtc);
            Assert.All(rows.Where(t => t.Period == "development"), t => Assert.True(t.EndUtc <= report.SplitUtc));
            Assert.All(rows.Where(t => t.Period == "final"), t => Assert.True(t.OriginUtc >= report.SplitUtc));
            foreach (string period in new[] { "development", "final" })
                Assert.Single(report.Scores.Where(s => s.HorizonHours == h && s.Period == period).Select(s => s.MatchedOrigins).Distinct());
        }
        Assert.Equal(before, JsonSerializer.Serialize(data));
    }

    [Fact]
    public void FutureTokensCannotChangeEarlierPredictorsOrTheHoldoutSplit()
    {
        var data = History(); var report = Run(data);
        var first = report.Origins.First(t => t.HorizonHours == .5 && t.Period == "final");
        var changed = data with { Tokens = data.Tokens.Select(t => t.ObservedAtUtc > first.OriginUtc
            ? t with { UncachedInputTokens = 999999, ReportedTotalTokens = 999999 } : t).ToArray() };
        var after = Run(changed); Assert.Equal(report.SplitUtc, after.SplitUtc);
        var same = after.Origins.Single(t => t.OriginUtc == first.OriginUtc && t.HorizonHours == first.HorizonHours);
        Assert.Equal(first.Probability, same.Probability); Assert.Equal(first.GlobalProbability, same.GlobalProbability);
        Assert.Equal(first.ConditionalTokens, same.ConditionalTokens); Assert.Equal(first.RecentPositiveTokens, same.RecentPositiveTokens);
        Assert.Equal(first.Continue30m, same.Continue30m); Assert.Equal(first.Continue2h, same.Continue2h);
        Assert.Equal(first.ActivityGatePassed, same.ActivityGatePassed);
    }

    [Fact]
    public void ReconstructionDoesNotRelabelLateOrUnknownClocksAsOperationalEvidence()
    {
        var data = History(); var through = data.CapturedAtUtc;
        var late = data with { CapturedAtUtc = through.AddDays(1), Tokens = data.Tokens.Select(t => t with
            { CapturedAtUtc = through.AddHours(1) }).ToArray() };
        var report = Run(late, through);
        Assert.NotEmpty(report.Origins); Assert.All(report.Origins, t => Assert.False(t.EarlierTokenClocksAvailable));
        Assert.All(report.Origins.Where(t => t.ObservedTokens > 0), t => Assert.False(t.PositiveTargetClocksTimely));
        Assert.All(report.Origins.Where(t => t.ObservedTokens == 0), t => Assert.Null(t.PositiveTargetClocksTimely));
        var unknown = data with { Tokens = data.Tokens.Select(t => t with { CapturedAtUtc = null }).ToArray() };
        var missing = Run(unknown); Assert.Equal(data.Tokens.Count, missing.UnknownCollectionEvents);
        Assert.All(missing.Origins, t => Assert.False(t.EarlierTokenClocksAvailable));
        var notYet = Run(late with { CapturedAtUtc = through }, through);
        Assert.Empty(notYet.Origins); Assert.Equal(0, notYet.UsedPositiveEvents);
    }

    [Fact]
    public void MissingRecentContextIsUnavailableWhileQuietTargetsRemainRecordedZero()
    {
        var data = History(); var report = Run(data);
        var quiet = report.Origins.Where(t => t.ObservedTokens == 0 && t.AgeGroup == 3).ToArray();
        Assert.NotEmpty(quiet); Assert.All(quiet, t => Assert.Null(t.Continue30m));
        Assert.All(quiet, t => Assert.NotNull(t.Continue2h));
        Assert.Contains(report.Scores.Where(s => s.Candidate == "continue-2h"), s => s.ExpectedOrigins > s.MatchedOrigins);
        var zero = data with { Tokens = data.Tokens.Select(t => t with { UncachedInputTokens = 0, ReportedTotalTokens = 0 }).ToArray() };
        var sparse = Run(zero); Assert.Empty(sparse.Origins);
        Assert.All(sparse.Scores, s => { Assert.Null(s.MatchedMae); Assert.Null(s.MatchedWape); Assert.Equal(0, s.ExpectedOrigins); });
    }

    [Fact]
    public void RecentPositiveComparatorUsesLatestEightRatherThanLargestEight()
    {
        var data = History(); var report = Run(data);
        var first = report.Origins.First(t => t.HorizonHours == .5);
        var prior = Enumerable.Range(0, 100).Select(i => Start.AddHours(2.5 + i * .5))
            .Where(t => t.AddHours(.5) <= first.OriginUtc).TakeLast(120)
            .Select(t => data.Tokens.Where(e => e.ObservedAtUtc > t && e.ObservedAtUtc <= t.AddHours(.5)).Sum(e => (double)e.ReportedTotalTokens))
            .Where(x => x > 0).ToArray();
        // This fixture's first grid origin is 02:30 and its first prediction has only global training.
        Assert.Equal(prior.Length, first.ActiveTrainingOrigins);
        Assert.Equal(prior.TakeLast(8).Average(), first.RecentPositiveTokens);
        Assert.Equal(prior.Average(), first.ConditionalTokens);
        var reversed = data with { Tokens = data.Tokens.Select(t => t with
            { UncachedInputTokens = 100000 - t.ReportedTotalTokens, ReportedTotalTokens = 100000 - t.ReportedTotalTokens }).ToArray() };
        var decreasing = Run(reversed).Origins.First(t => t.HorizonHours == .5);
        Assert.True(first.RecentPositiveTokens > first.ConditionalTokens);
        Assert.True(decreasing.RecentPositiveTokens < decreasing.ConditionalTokens);
    }

    [Fact]
    public void ExistingLiveHorizonsAndPredictionsRemainOwnedByTheSameService()
    {
        var data = History();
        var live = SessionWorkloadPredictionService.Evaluate(data, data.CapturedAtUtc);
        var explicitSame = SessionWorkloadPredictionService.ReconstructHorizons(data, data.CapturedAtUtc, [.5, 1], CancellationToken.None);
        Assert.Equal(JsonSerializer.Serialize(live), JsonSerializer.Serialize(explicitSame));
        Assert.Throws<ArgumentException>(() => SessionWorkloadPredictionService.ReconstructHorizons(data, data.CapturedAtUtc, [.5, .5], CancellationToken.None));
        Assert.Throws<ArgumentException>(() => SessionWorkloadPredictionService.ReconstructHorizons(data, data.CapturedAtUtc, [double.NaN], CancellationToken.None));
        Assert.Throws<ArgumentException>(() => UsualActivityEvaluation.Evaluate(data, Start.AddDays(10), Start));
    }
}
