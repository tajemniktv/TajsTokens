using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class ComposedQuotaPolicyTests
{
    [Fact]
    public void PromotionRequiresIndependentRecentPairedEvidenceNotRepeatedPolling()
    {
        var now = DateTimeOffset.Parse("2026-09-18T10:00:00Z");
        var cohort = new QuotaHistoryCohort("codex", "default", QuotaWindowKind.Weekly,
            "codex-app-server:codex", "account", "codex", "pro", null, 10080);
        var workload = new PredictedWorkload(now, 0.5, 10, [10, 0, 0, 0, 0], new Dictionary<string, double>(),
            new Dictionary<string, double>(), 1, now, ForecastReplayAvailability.ReconstructedEventTime, "fixture");
        var trials = Enumerable.Range(0, 16).Select(i => new ComposedQuotaTrial(now.AddDays(-7 + i / 2).AddMinutes(-60 + i % 2 * 30),
            now.AddDays(-7 + i / 2).AddMinutes(-30 + i % 2 * 30), now.AddDays(-7 + i / 2),
            1, 1, 1, 0.1, 0.1, 1, 99, workload) { IncumbentIntervalLoss = 1 }).ToArray();
        var score = new ComposedQuotaScore(cohort, 0.5, "total", 20, 16, 8, 0, 0.1, 0.1, 1, 0.1, trials)
        { IncumbentIntervalLoss = 1 };
        Assert.True(ComposedQuotaPolicy.SupportsSelection(score, now));
        Assert.False(ComposedQuotaPolicy.SupportsStrictSelection(score, now));
        var strict = score with
        {
            Availability = ForecastReplayAvailability.CollectedByOrigin,
            Trials = trials.Select(x => x with
            {
                Availability = ForecastReplayAvailability.CollectedByOrigin,
                Workload = x.Workload with { Availability = ForecastReplayAvailability.CollectedByOrigin }
            }).ToArray()
        };
        Assert.True(ComposedQuotaPolicy.SupportsStrictSelection(strict, now));
        Assert.False(ComposedQuotaPolicy.SupportsStrictSelection(strict with
        {
            Trials = strict.Trials.Select(x => x.ResetUtc == now ? x with { IntervalLoss = 1.1 } : x).ToArray()
        }, now));
        Assert.False(ComposedQuotaPolicy.SupportsStrictSelection(strict with { AssertedTrainingIntervals = 10 }, now));
        Assert.False(ComposedQuotaPolicy.SupportsStrictSelection(strict with { Trials = trials }, now));
        Assert.False(ComposedQuotaPolicy.SupportsSelection(score, now.AddDays(1)));
        Assert.False(ComposedQuotaPolicy.SupportsSelection(score with
        { Trials = trials.Select(x => x with { ResetUtc = now }).ToArray() }, now));
        Assert.False(ComposedQuotaPolicy.SupportsSelection(score with
        { Trials = trials.Select(x => x with { IncumbentIntervalLoss = null }).ToArray() }, now));
        Assert.False(ComposedQuotaPolicy.SupportsSelection(score with
        { Trials = trials.Select(x => x with { IntervalLoss = 0.95 }).ToArray() }, now));
    }

    [Fact]
    public void UnsupportedCurrentAccountKeepsExactIncumbent()
    {
        var now = DateTimeOffset.UtcNow;
        var anchor = new QuotaSnapshot(QuotaWindowKind.Weekly, now, 20, 10080, now.AddDays(1), "codex", "default", "codex-app-server:codex");
        IReadOnlyList<QuotaHorizonPrediction> incumbent = [new(0.5, now.AddMinutes(30), 79, 1, "pace", false, 0, 0, null, null, null, 0, "baseline")];
        Assert.Same(incumbent, ComposedQuotaPolicy.Apply(new([], [], [], [], now, "fixture"), anchor, incumbent));
    }
}
