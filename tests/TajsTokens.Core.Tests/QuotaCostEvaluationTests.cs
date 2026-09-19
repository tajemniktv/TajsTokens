using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaCostEvaluationTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

    [Fact]
    public void ConstructionCoveragePartitionsCandidateStartsWithoutChangingSelectedTargets()
    {
        var data = Data(Enumerable.Range(0, 13).Select(i => Quota(i * 15, 20)).ToArray(), []);
        var result = QuotaCostObservationBuilder.BuildDetailed(data, horizons: [.5]);
        var coverage = Assert.Single(result.Coverage);
        Assert.Equal(11, coverage.CandidateStarts);
        Assert.Equal(5, coverage.BuiltIntervals);
        Assert.Equal(5, coverage.RejectedStarts["overlaps-selected-interval"]);
        Assert.Equal(1, coverage.RejectedStarts["no-outcome-in-epoch"]);
        Assert.Equal(coverage.CandidateStarts, coverage.BuiltIntervals + coverage.RejectedStarts.Values.Sum());
        Assert.Equal(new[] { 15, 45, 75, 105, 135 }, result.Observations.Select(x => (int)(x.StartUtc - Start).TotalMinutes));
        Assert.All(result.Observations, x => Assert.Equal(TimeSpan.FromMinutes(30), x.EndUtc - x.StartUtc));
    }

    [Fact]
    public void ConstructionDistinguishesWarmupAndPollingGapFromAbsentWorkload()
    {
        var data = Data(new[] { 0, 5, 15, 45, 60, 90 }.Select(i => Quota(i, 20)).ToArray(), []);
        var result = QuotaCostObservationBuilder.BuildDetailed(data, horizons: [.5]);
        var coverage = Assert.Single(result.Coverage);
        Assert.Equal(4, coverage.CandidateStarts);
        Assert.Equal(2, coverage.BuiltIntervals);
        Assert.Equal(1, coverage.RejectedStarts["epoch-warmup"]);
        Assert.Equal(1, coverage.RejectedStarts["outcome-beyond-poll-tolerance"]);
        Assert.All(result.Observations, x => Assert.Contains("no-recorded-tokens-not-proven-idle", x.QualityFlags));
        var empty = QuotaCostObservationBuilder.BuildDetailed(Data([], []));
        Assert.Empty(empty.Observations);
        Assert.Empty(empty.Coverage);
    }

    [Fact]
    public void DatasetUsesHalfOpenWorkloadAndKeepsFlatMeterUncertainty()
    {
        var quota = Enumerable.Range(0, 13).Select(i => Quota(i * 15, 20)).ToArray();
        var token = Token(15, 100);
        var data = Data(quota, [token, Token(16, 200), Token(45, 300), Token(46, 999)]);
        var rows = QuotaCostObservationBuilder.Build(data);
        var first = rows.First(x => x.HorizonHours == 0.5);
        Assert.Equal(Start.AddMinutes(15), first.StartUtc);
        Assert.Equal(Start.AddMinutes(45), first.EndUtc);
        Assert.Equal(500, first.TokenCategories[0]);
        Assert.Equal(0, first.LowerDelta);
        Assert.Equal(1, first.UpperDelta);
        Assert.Equal(0, first.IntervalLoss(0.7));
        foreach (var horizon in rows.GroupBy(x => x.HorizonHours))
            Assert.All(horizon.Zip(horizon.Skip(1)), pair => Assert.True(pair.First.EndUtc <= pair.Second.StartUtc));
    }

    [Fact]
    public void ResetConflictPlanAndInvalidReadingsCannotBeBridged()
    {
        var quota = Enumerable.Range(0, 13).Select(i => Quota(i * 15, 20 + i)).ToArray();
        quota[4] = quota[4] with { UsedPercent = null };
        quota[8] = quota[8] with { PlanType = "other" };
        var rows = QuotaCostObservationBuilder.Build(Data(quota, []));
        Assert.DoesNotContain(rows, x => x.StartUtc < quota[4].CapturedAtUtc && x.EndUtc > quota[4].CapturedAtUtc);
        // Different cohorts are separate observations, not silently interpolated into each other.
        Assert.All(rows, x => Assert.Equal("pro", x.Cohort.PlanType));
        Assert.DoesNotContain(rows, x => x.StartUtc < quota[8].CapturedAtUtc && x.EndUtc > quota[8].CapturedAtUtc);
        Assert.DoesNotContain(rows, x => x.HorizonHours == 2);
    }

    [Fact]
    public void CachedEmbeddedReadingsAreNotZeroTargetsAndPrecisionIsNotInvented()
    {
        var quota = Enumerable.Range(0, 9).Select(i => Quota(i * 15, 20) with
        {
            Source = "codex-rollout:primary", SourceIdentity = "file", SessionId = "session", ObservationId = $"row-{i}", AccountKey = null
        }).ToArray();
        Assert.Empty(QuotaCostObservationBuilder.Build(Data(quota, [])));
        quota = quota.Select((q, i) => q with { UsedPercent = 20 + i * 0.25 }).ToArray();
        var row = QuotaCostObservationBuilder.Build(Data(quota, [])).First();
        Assert.Contains("meter-precision-unverified-sensitivity-only", row.QualityFlags);
        Assert.Contains("native-account-id-absent", row.QualityFlags);
        var confirmed = QuotaCostObservationBuilder.Build(Data(quota, []), userConfirmedRolloutOwnership: true).First();
        Assert.Contains("user-confirmed-rollout-ownership-native-account-id-absent", confirmed.QualityFlags);
        Assert.Null(confirmed.Cohort.AccountKey);
        Assert.Equal("session", confirmed.Cohort.SessionId);
    }

    [Fact]
    public void SaturationDropsAndJitterChainingDoNotCreateOrdinaryCostTargets()
    {
        var quota = Enumerable.Range(0, 9).Select(i => Quota(i * 15, 90 + i)).ToArray();
        quota[3] = quota[3] with { UsedPercent = 100 };
        quota[4] = quota[4] with { UsedPercent = 1 };
        var rows = QuotaCostObservationBuilder.Build(Data(quota, []));
        Assert.DoesNotContain(rows, x => x.EndUsed >= 100);
        var construction = QuotaCostObservationBuilder.BuildDetailed(Data(quota, []));
        Assert.Contains(construction.Coverage, x => x.RejectedStarts.ContainsKey("saturated-outcome"));
        Assert.All(construction.Coverage, x => Assert.Equal(x.CandidateStarts, x.BuiltIntervals + x.RejectedStarts.Values.Sum()));
        Assert.DoesNotContain(rows, x => x.StartUtc < quota[4].CapturedAtUtc && x.EndUtc >= quota[4].CapturedAtUtc);
        quota = Enumerable.Range(0, 9).Select(i => Quota(i * 15, 20 + i) with
        { ResetsAtUtc = Start.AddDays(7).AddSeconds(i) }).ToArray();
        Assert.Empty(QuotaCostObservationBuilder.Build(Data(quota, [])));
    }

    [Fact]
    public void BandsNeedEarlierIndependentGenerationsNotManyRowsInOneReset()
    {
        var rows = Observations(130);
        var report = QuotaCostEvaluation.Evaluate(rows, "fixture");
        var score = report.Scores.Single(x => x.Candidate == "total");
        Assert.True(score.BandSamples > 0);
        Assert.All(score.Trials.Where(x => x.StartUtc < Start.AddDays(10)), x => Assert.Null(x.LowerPrediction));
        var oneReset = rows.Select(x => x with { ResetUtc = Start.AddDays(100) }).ToArray();
        Assert.All(QuotaCostEvaluation.Evaluate(oneReset, "fixture").Scores, x =>
        {
            Assert.Equal(0, x.BandSamples);
            Assert.False(x.MaterialWin);
        });
    }

    [Fact]
    public void FrozenFitDoesNotSeeFutureOutcomesVocabularyOrCollectionTimes()
    {
        var rows = Observations(60);
        var original = QuotaCostEvaluation.Evaluate(rows, "fixture");
        var changed = rows.Select((x, i) => i < 40 ? x : x with
        {
            EndUsed = 99, LowerDelta = 90, UpperDelta = 92,
            Features = x.Features with { ModelTokenShares = new Dictionary<string, double> { ["future-model"] = 1 } }
        }).ToArray();
        var after = QuotaCostEvaluation.Evaluate(changed, "fixture");
        foreach (var score in original.Scores)
        {
            var other = after.Scores.Single(x => x.Candidate == score.Candidate);
            Assert.Equal(score.Coefficients, other.Coefficients);
            Assert.Equal(score.Trials.Take(20).Select(x => x.Prediction), other.Trials.Take(20).Select(x => x.Prediction));
            Assert.DoesNotContain("model:future-model", other.Coefficients.Keys);
            Assert.All(score.Coefficients.Values, x => Assert.True(x >= 0));
        }
    }

    [Fact]
    public void EmptyLocalWorkDoesNotBecomeTimeCostAndSparseCohortsDoNotBorrow()
    {
        var rows = Observations(60);
        rows[30] = rows[30] with { TokenCategories = new double[5], Features = rows[30].Features with { Tokens = 0 } };
        var unknown = rows.Take(10).Select(x => x with { Cohort = x.Cohort with { AccountKey = null } });
        var report = QuotaCostEvaluation.Evaluate(rows.Concat(unknown).ToArray(), "fixture");
        Assert.All(report.Scores.Where(x => x.Cohort.AccountKey is null), x => Assert.Equal(0, x.HeldOutSamples));
        Assert.All(report.Scores.Where(x => x.Cohort.AccountKey is not null && x.Candidate is not "pace" and not "persistence"),
            score => Assert.Equal(0, score.Trials.Single(x => x.StartUtc == rows[30].StartUtc).Prediction));
    }

    [Fact]
    public void ReplicatedResidualShiftIsVisibleWithoutRefitting()
    {
        var rows = Observations(80);
        // Training: two generations; reference: next three; shift: three later generations.
        rows = rows.Select((x, i) => i >= 50 ? x with { EndUsed = 30, LowerDelta = 19, UpperDelta = 21 } : x).ToArray();
        var score = QuotaCostEvaluation.Evaluate(rows, "fixture").Scores.Single(x => x.Candidate == "total");
        Assert.NotEmpty(score.CandidateShiftResets);
        Assert.Equal(0, score.BandSamples);
        Assert.False(score.MaterialWin);
    }

    internal static QuotaCostObservation[] Observations(int count)
    {
        var cohort = QuotaHistoryPolicy.Cohort(Quota(0, 10));
        return Enumerable.Range(0, count).Select(i =>
        {
            var time = Start.AddDays(i / 10).AddMinutes(i % 10 * 30);
            var tokens = 1_000_000L * (1 + i % 3);
            var features = CodexForecastFeatureBuilder.Build(Data([], [Token(1, tokens)]), Start.AddMinutes(30), 0.5);
            var delta = tokens / 1e6 * 2;
            return new QuotaCostObservation(cohort, Start.AddDays(i / 10), Start.AddDays(i / 10 + 1),
                time, time.AddMinutes(30), 0.5, 10, 10 + delta, delta - 1, delta + 1,
                "app-server-rounding-envelope", 10, [tokens, 0, 0, 0, 0], features, null, 0, []);
        }).ToArray();
    }

    private static QuotaSnapshot Quota(int minutes, double used) => new(QuotaWindowKind.Weekly,
        Start.AddMinutes(minutes), used, 10080, Start.AddDays(7), "codex", "default", "codex-app-server:codex", "account")
        { HasSourceTimestamp = true, CollectedAtUtc = Start.AddMinutes(minutes), PlanType = "pro" };

    private static CodexPredictiveTokenEvent Token(int minutes, long value) => new("session", Start.AddMinutes(minutes),
        Start.AddDays(1), "model", "high", value, 0, 0, 0, 0, value);

    private static CodexForecastDataset Data(IReadOnlyList<QuotaSnapshot> quota, IReadOnlyList<CodexPredictiveTokenEvent> tokens) =>
        new(quota, [], tokens, [], Start.AddDays(30), "fixture");
}
