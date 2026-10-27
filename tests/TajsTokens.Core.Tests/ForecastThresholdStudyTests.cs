using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class ForecastThresholdStudyTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
    private static CodexForecastDataset Data(int count = 48) => new(
        Enumerable.Range(0, count).Select(i => new QuotaSnapshot(QuotaWindowKind.Weekly, Start.AddMinutes(i * 15),
            10 + i * .5, 10080, Start.AddDays(7), "codex", "default", "codex-app-server", "private-account")
        { CollectedAtUtc = Start.AddMinutes(i * 15), HasSourceTimestamp = true }).ToArray(), [], [], [], Start.AddDays(1), "fixture");

    [Fact]
    public void ExplicitDefaultsExactlyReproduceExistingPaceResetAndScenarioResults()
    {
        CodexForecastDataset data = Data();
        Assert.Equal(QuotaPredictionService.Replay(data, .5, ForecastReplayAvailability.CollectedByOrigin),
            QuotaPredictionService.Replay(data, .5, ForecastReplayAvailability.CollectedByOrigin, options: ForecastEvaluationOptions.Default));
        DateTimeOffset at = data.Quota[^1].CapturedAtUtc;
        Assert.Equal(QuotaPredictionService.BuildResetOutlook(data.Quota, at),
            QuotaPredictionService.ReplayResetOrigins(data.Quota, [at])[at]);
        var request = new ScenarioRequest(1, 1, 0);
        Assert.Equal(QuotaPredictionService.Simulate(request, [], at),
            QuotaPredictionService.Simulate(request, [], at, ForecastEvaluationOptions.Default));
    }

    [Fact]
    public void FutureLabelsAndExtraQueryOriginsCannotChangeEarlierOutputs()
    {
        CodexForecastDataset all = Data();
        DateTimeOffset at = all.Quota[16].CapturedAtUtc;
        CodexForecastDataset prefix = all with { Quota = all.Quota.Where(x => x.CapturedAtUtc <= at).ToArray() };
        QuotaHorizonPrediction before = QuotaPredictionService.ReplayOrigins(prefix, .5, ForecastReplayAvailability.CollectedByOrigin, [at])[at];
        var after = QuotaPredictionService.ReplayOrigins(all, .5, ForecastReplayAvailability.CollectedByOrigin, [at, all.Quota[24].CapturedAtUtc]);
        Assert.Equal(before, after[at]);
        Assert.Equal(QuotaPredictionService.BuildResetOutlook(prefix.Quota, at), QuotaPredictionService.ReplayResetOrigins(all.Quota, [at])[at]);
    }

    [Fact]
    public void StudyHasStableDenominatorsUnmatchedOriginsAndNoRawIdentifiers()
    {
        ForecastThresholdReport report = ForecastThresholdStudy.Evaluate(Data());
        Assert.NotEmpty(report.Scores);
        foreach (var group in report.Scores.GroupBy(x => (x.Cohort, x.Target, x.Family, x.Period)))
            Assert.Single(group.Select(x => x.CandidateOrigins).Distinct());
        Assert.Contains(report.Scores, x => x.UnmatchedPredictions > 0);
        Assert.All(report.Scores, x => { Assert.True(x.BandOrigins <= x.PointOrigins); Assert.True(x.PairedOutcomes <= x.PointOrigins); });
        Assert.DoesNotContain("private-account", JsonSerializer.Serialize(report));
        Assert.DoesNotContain("codex-app-server", JsonSerializer.Serialize(report));
        Assert.Equal(ForecastThresholdStudy.Fingerprint(Data()), report.DatasetIdentity);
        Assert.All(report.Splits, x => Assert.Contains("inconclusive", x.Status));
    }

    [Fact]
    public void OptionsAreIsolatedAndEachStudyAlternativeChangesOnlyOneValue()
    {
        string defaults = JsonSerializer.Serialize(ForecastEvaluationOptions.Default);
        foreach (ThresholdConfiguration candidate in ForecastThresholdStudy.Configurations.Skip(1))
        {
            var original = typeof(ForecastEvaluationOptions).GetProperties().Where(x => x.Name != "Default").ToArray();
            Assert.Single(original, p => !Equals(p.GetValue(candidate.Options), p.GetValue(ForecastEvaluationOptions.Default)));
        }
        _ = QuotaPredictionService.Replay(Data(), .5, ForecastReplayAvailability.CollectedByOrigin,
            options: ForecastEvaluationOptions.Default with { IntervalSamples = 2 });
        Assert.Equal(defaults, JsonSerializer.Serialize(ForecastEvaluationOptions.Default));
        Assert.Throws<ArgumentOutOfRangeException>(() => (ForecastEvaluationOptions.Default with { BlockHours = 0 }).Validate());
    }
}
