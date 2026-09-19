using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaEvaluationCoverageTests
{
    [Fact]
    public void DatasetCoverageWeightsTokensAndNeverInfersTierOrTimeliness()
    {
        var now = DateTimeOffset.UtcNow;
        CodexPredictiveTokenEvent Token(long count, string? model, string? effort, DateTimeOffset? collected) =>
            new("s", now, collected, model, effort, count, 0, 0, 0, 0, count);
        CodexWorkloadObservation Setting(string? tier, string kind = "thread_settings_applied") =>
            new("r", "source", "file", 0, 1, "s", kind, null, now, null, null, null, null, null, null, ServiceTier: tier);
        var data = new CodexForecastDataset([], [Setting("priority"), Setting(null), Setting("invented", "task_complete")],
            [Token(1, "model", null, now), Token(999, null, "low", null),
                Token(10, " ", "", now.AddMinutes(1)) with { NonReasoningOutputTokens = 1 }], [], now, "fixture");
        var result = QuotaEvaluationCoverageBuilder.Build(data);
        Assert.Equal(1010m, result.ReportedTokens);
        Assert.Equal(1m, result.TokensWithModel);
        Assert.Equal(999m, result.TokensWithEffort);
        Assert.Equal(1, result.CategoryMismatchRecords);
        Assert.Equal(1, result.UnknownCollectionRecords);
        Assert.Equal(1, result.CollectedAfterEventRecords);
        Assert.Equal(1, result.RequestedTierSettings["priority"]);
        Assert.Single(result.RequestedTierSettings);
        Assert.Equal(1, result.MissingTierSettings);
        Assert.Equal(2, result.UndatedTierSettings);
    }

    [Fact]
    public void CohortFlagsOverlapWithoutMultiplyingByCandidateAndStaySourceScoped()
    {
        var rows = QuotaCostEvaluationTests.Observations(3);
        rows[0] = rows[0] with { QualityFlags = ["missing-context", "missing-context", "missing-runtime"] };
        rows[1] = rows[1] with { Cohort = rows[1].Cohort with { AccountKey = null },
            QualityFlags = ["user-asserted-account-association"] };
        rows[2] = rows[2] with { HorizonHours = 2 };
        var report = QuotaCostEvaluation.Evaluate(rows, "fixture");
        Assert.Equal(3, report.CohortCoverage.Count);
        var known = report.CohortCoverage.Single(x => x.HorizonHours == .5 && x.Cohort.AccountKey is not null);
        Assert.Equal(1, known.Intervals);
        Assert.Equal(1, known.NativeAccountIntervals);
        Assert.Equal(1, known.QualityCounts["missing-context"]);
        Assert.Equal(1, known.QualityCounts["missing-runtime"]);
        var asserted = report.CohortCoverage.Single(x => x.Cohort.AccountKey is null);
        Assert.Equal(0, asserted.NativeAccountIntervals);
        Assert.Equal(1, asserted.AssertedAccountIntervals);
        Assert.Null(report.EvidenceCoverage); // Row-only replay cannot invent dataset coverage.
    }

    [Fact]
    public void EmptyDatasetHasNoInventedSettingsOrCoverage()
    {
        var result = QuotaEvaluationCoverageBuilder.Build(new([], [], [], [], DateTimeOffset.UtcNow, "empty"));
        Assert.Equal(0, result.TokenRecords);
        Assert.Equal(0m, result.ReportedTokens);
        Assert.Empty(result.RequestedTierSettings);
    }
}
