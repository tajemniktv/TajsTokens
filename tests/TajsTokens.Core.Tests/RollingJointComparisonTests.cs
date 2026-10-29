using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Core.Research;

namespace TajsTokens.Core.Tests;

public sealed class RollingJointComparisonTests
{
    private static RollingJointReport Run(CodexForecastDataset data, Action<RollingJointFitAudit>? audit = null) =>
        RollingJointComparison.Evaluate(data, ForecastReplayAvailability.CollectedByOrigin, audit: audit, includeTrials: true);

    [Fact]
    public void ExpandingPrefixRetainsNominalTargetsAndMatureTrainingAcrossFixedHoldout()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        string before = JsonSerializer.Serialize(data);
        var report = Run(data);
        var group = Assert.Single(report.Cohorts, c => c.HorizonHours == .5);
        Assert.Equal("released-head", group.DevelopmentChoice);
        Assert.NotEmpty(group.ChoiceReasons);
        var prefix = group.Trials.Where(t => t.Candidate == "prefix-joint-2h" && t.Remaining is not null).ToArray();
        Assert.NotEmpty(prefix); Assert.Contains(prefix, t => t.TrainingRows > 20);
        Assert.All(prefix, t =>
        {
            Assert.Equal(t.OriginUtc.AddHours(.5), t.TargetUtc);
            Assert.True(t.TrainingToUtc <= t.OriginUtc); Assert.True(t.TrainingAvailableUtc <= t.OriginUtc);
        });
        Assert.Contains(prefix, t => t.Period == "final" && t.TrainingToUtc >= group.SplitUtc);
        Assert.All(group.Trials.Where(t => t.Candidate == "first20-joint-2h"), t => Assert.Equal(20, t.TrainingRows));
        Assert.Contains(group.Trials, t => t.Period == "boundary");
        Assert.Equal(before, JsonSerializer.Serialize(data));
    }

    [Fact]
    public void FutureLabelsAndWorkCannotChangeDevelopmentChoiceOrEarliestFinalPrediction()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        var original = Assert.Single(Run(data).Cohorts, c => c.HorizonHours == .5);
        var first = original.Trials.First(t => t.Candidate == "prefix-joint-2h" && t.Period == "final");
        var changed = data with
        {
            Quota = data.Quota.Select(q => q.CapturedAtUtc > first.OriginUtc ? q with { UsedPercent = q.UsedPercent + 10 } : q).ToArray(),
            Tokens = data.Tokens.Select(t => t.ObservedAtUtc > first.OriginUtc ? t with { Model = "future", UncachedInputTokens = 50000, ReportedTotalTokens = 50000 } : t).ToArray(),
        };
        var after = Assert.Single(Run(changed).Cohorts, c => c.HorizonHours == .5);
        Assert.Equal(original.SplitUtc, after.SplitUtc); Assert.Equal(original.DevelopmentChoice, after.DevelopmentChoice);
        var same = after.Trials.Single(t => t.Candidate == first.Candidate && t.OriginUtc == first.OriginUtc);
        Assert.Equal(first.Remaining, same.Remaining); Assert.Equal(first.LowerRemaining, same.LowerRemaining);
        Assert.Equal(first.HeadRemaining, same.HeadRemaining); Assert.Equal(first.DirectRemaining, same.DirectRemaining);
        Assert.Equal(first.TrainingToUtc, same.TrainingToUtc);
    }

    [Fact]
    public void OperationalReplayUsesCollectionClocksWithoutBackdatingMeasurements()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        var delayed = data with { Quota = data.Quota.Select(q => q with { CollectedAtUtc = q.CapturedAtUtc.AddSeconds(30) }).ToArray() };
        var report = Run(delayed);
        Assert.NotEmpty(report.Cohorts);
        Assert.All(report.Cohorts.SelectMany(c => c.Trials), t => Assert.Equal(t.OriginUtc.AddSeconds(-30), t.OriginalMeasuredOriginUtc));
        var tooLate = data with { Quota = data.Quota.Select(q => q with { CollectedAtUtc = q.CapturedAtUtc.AddMinutes(6) }).ToArray() };
        var unqualified = Run(tooLate);
        Assert.Empty(unqualified.Cohorts); Assert.Equal(0, unqualified.ClockQualifiedQuotaRows);
        Assert.Equal(data.Quota.Count, unqualified.InputQuotaRows);
        Assert.NotEmpty(RollingJointComparison.Evaluate(tooLate, ForecastReplayAvailability.ReconstructedEventTime).Cohorts);
    }

    [Fact]
    public void LateTokenTrainingDoesNotBecomeFreeWorkOrQualifiedJointEvidence()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        var late = data with { Tokens = data.Tokens.Select(t => t with { CapturedAtUtc = data.CapturedAtUtc.AddDays(1) }).ToArray() };
        var groups = Run(late).Cohorts;
        Assert.NotEmpty(groups);
        Assert.All(groups.SelectMany(c => c.Trials).Where(t => t.Candidate.StartsWith("prefix", StringComparison.Ordinal)), t =>
        {
            Assert.Null(t.Remaining); Assert.Equal("insufficient-mature-training", t.WithheldReason);
        });
        Assert.Contains(groups.SelectMany(c => c.Trials), t => t.Candidate == "released-head" && t.Remaining is not null);
        var policy = groups.Single(c => c.HorizonHours == .5).PolicyScores.Single(s => s.Candidate == "prefix-joint-2h" && s.Period == "final");
        Assert.True(policy.PointOrigins > 0); Assert.Equal(policy.HeadMae, policy.PairedMae);
    }

    [Fact]
    public void PollingDelayDoesNotExtendAProjectedWorkloadHorizon()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        data = data with { Quota = data.Quota.Select((q, i) => q with
            { CapturedAtUtc = q.CapturedAtUtc.AddMinutes(i / 2), CollectedAtUtc = q.CapturedAtUtc.AddMinutes(i / 2) }).ToArray() };
        var captures = new List<RollingJointFitAudit>();
        var report = Run(data, captures.Add);
        Assert.Contains(report.Cohorts.SelectMany(c => c.Trials), t => t.OutcomeUtc > t.TargetUtc);
        foreach (var capture in captures.Where(c => c.Candidate is "first20-joint-2h" or "prefix-joint-2h"))
        {
            double actual = data.Tokens.Where(t => t.ObservedAtUtc > capture.OriginUtc.AddHours(-2) && t.ObservedAtUtc <= capture.OriginUtc)
                .Sum(t => (double)t.ReportedTotalTokens) * capture.HorizonHours / 2;
            Assert.Equal(actual, (double)capture.Projected.ReportedTokens, 6);
        }
    }

    [Fact]
    public void DevelopmentChoiceScoresFallbacksAndIgnoresFutureScores()
    {
        var origin = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var rows = new List<RollingJointTrial>();
        for (int i = 0; i < 64; i++)
        foreach (string candidate in new[] { "released-head", "direct", "prefix-categories-2h" })
        {
            double remaining = candidate == "prefix-categories-2h" ? 80 : 78;
            rows.Add(new(candidate, origin.AddHours(i), origin.AddHours(i + .5), origin.AddHours(i + .5), origin.AddDays(7),
                "development", remaining, 80, 90, 78, 78, null, null, 20, origin, origin, origin, null, 10, 10,
                Math.Abs(remaining - 80), null, null, origin.AddHours(i), origin.AddHours(i + .5)));
        }
        Assert.Equal("prefix-categories-2h", RollingJointComparison.ChooseRollingDevelopment(rows).Candidate);
        var future = rows.Concat(rows.Where(t => t.Candidate == "prefix-categories-2h").Select(t => t with
            { Period = "final", Remaining = 0, IntervalLoss = 80 })).ToArray();
        Assert.Equal("prefix-categories-2h", RollingJointComparison.ChooseRollingDevelopment(future).Candidate);
        rows[2] = rows[2] with { Remaining = null, IntervalLoss = null };
        Assert.Equal("prefix-categories-2h", RollingJointComparison.ChooseRollingDevelopment(rows).Candidate);
        var sparseComposition = rows.Select((t, i) => t.Candidate == "prefix-categories-2h" && i > 20
            ? t with { Remaining = null, IntervalLoss = null } : t).ToArray();
        Assert.Equal("released-head", RollingJointComparison.ChooseRollingDevelopment(sparseComposition).Candidate);
        rows[2] = rows[2] with { Remaining = 80, IntervalLoss = 0 };
        var rich = rows.Concat(rows.Where(t => t.Candidate == "prefix-categories-2h")
            .Select(t => t with { Candidate = "prefix-joint-2h" })).ToArray();
        Assert.Equal("prefix-categories-2h", RollingJointComparison.ChooseRollingDevelopment(rich).Candidate);
        rich = rich.Select(t => t.Candidate == "prefix-categories-2h" ? t with { Remaining = 79.8, IntervalLoss = .2 } : t).ToArray();
        Assert.Equal("prefix-joint-2h", RollingJointComparison.ChooseRollingDevelopment(rich).Candidate);
    }

    [Fact]
    public void ResidualBandsNeedMatureBlocksAndRemainSeparateFromConditionalPathSpread()
    {
        var original = ComposedQuotaEvaluatorTests.TimelyData();
        var start = original.Quota[0].CapturedAtUtc;
        var data = original with
        {
            Quota = Enumerable.Range(0, 300).Select(i => original.Quota[0] with
                { CapturedAtUtc = start.AddMinutes(i * 15), CollectedAtUtc = start.AddMinutes(i * 15), UsedPercent = i * .1 }).ToArray(),
            Tokens = Enumerable.Range(0, 300).Select(i => original.Tokens[0] with
                { ObservedAtUtc = start.AddMinutes(i * 15), CapturedAtUtc = start.AddMinutes(i * 15) }).ToArray(),
            CapturedAtUtc = start.AddDays(4),
        };
        var report = Run(data);
        var halfHour = Assert.Single(report.Cohorts, c => c.HorizonHours == .5);
        var points = halfHour.Trials.Where(t => t.Candidate == "prefix-joint-2h" && t.Remaining is not null).ToArray();
        var firstBand = points.First(t => t.LowerRemaining is not null);
        var priorBlocks = ChronologicalEvidence.Blocks(points.Where(t => t.OutcomeUtc <= firstBand.OriginUtc),
            t => t.OriginUtc, t => t.OutcomeUtc, t => t.ResetUtc);
        Assert.True(priorBlocks.Count >= 8);
        var paths = halfHour.Scores.Single(s => s.Candidate == "prefix-blocks-2h" && s.Period == "final");
        Assert.True(paths.RawPathHeadOrigins > 0); Assert.True(paths.BandedHeadOrigins > 0);
        Assert.Equal(0, paths.RawPathWidth); Assert.Equal(0, paths.RawPathCoverage);
        Assert.Equal(1, paths.IntervalCoverage);
    }

    [Fact]
    public void ZeroRecordedTrainingAndSparseHistoryRemainExplicit()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        var noWork = Run(data with { Tokens = [] });
        Assert.All(noWork.Cohorts.SelectMany(c => c.Trials).Where(t => t.Candidate.StartsWith("prefix", StringComparison.Ordinal)), t =>
        { Assert.Null(t.Remaining); Assert.Equal("no-recorded-training-work", t.WithheldReason); });
        var sparse = Run(data with { Quota = data.Quota.Take(5).ToArray() });
        Assert.All(sparse.Cohorts, c => { Assert.Null(c.SplitUtc); Assert.Empty(c.Trials); Assert.Equal(0, c.FinalPolicy.CandidateOrigins); });
    }
}
