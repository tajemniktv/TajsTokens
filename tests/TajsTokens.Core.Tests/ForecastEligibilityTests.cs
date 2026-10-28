using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class ForecastEligibilityTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
    private static CodexForecastDataset Data() => new(
        Enumerable.Range(0, 40).Select(i => new QuotaSnapshot(QuotaWindowKind.Weekly, Start.AddMinutes(i * 30),
            10 + i, 10080, Start.AddDays(7), "codex", "default", "codex-app-server:private-source", "private-account")
        { CollectedAtUtc = Start.AddMinutes(i * 30), HasSourceTimestamp = true }).ToArray(), [], [], [], Start.AddDays(1), "fixture");

    private static CodexWorkloadObservation Meta(string session, string source) => new(
        "private-record", "private-file-id", "private-path", 0, 1, session, "session_meta", Start, Start,
        null, null, null, null, null, null, SessionSourceKind: source);

    [Fact]
    public void NoTimelyEventsAndUnknownRolesHaveDistinctOverlappingReasonsAndNumericExamples()
    {
        CodexForecastDataset data = Data();
        var late = new CodexPredictiveTokenEvent("private-session", Start.AddHours(17), Start.AddDays(2), null, null, 1, 0, 0, 0, 0, 1);
        data = data with { Tokens = [late, late with { CapturedAtUtc = null }] };
        ForecastEligibilityReport report = ForecastThresholdStudy.ExplainEligibility(data);
        ForecastOriginEligibility final = report.Origins.Single(x => x.Target == "0.5h" && x.Period == "final");
        Assert.Equal(final.CandidateOrigins, final.OriginWorkloadRejected);
        Assert.Equal(final.CandidateOrigins, final.OriginReasonOverlaps["no-timely-token-events"]);
        Assert.Equal(final.CandidateOrigins, final.OriginReasonOverlaps["no-classified-token-sessions"]);
        Assert.False(final.OriginReasonOverlaps.ContainsKey("unknown-role-token-sessions"));
        Assert.True(final.InputConditionOverlaps["rejected-later-collected-tokens-present"] > 0);
        Assert.Equal(final.InputConditionOverlaps["rejected-later-collected-tokens-present"],
            final.InputConditionOverlaps["rejected-unknown-collection-tokens-present"]);
        Assert.Contains(final.Examples, x => x.RetainedTokenEvents == 2 && x.TimelyTokenEvents == 0 &&
            x.UnknownCollectionEvents == 1 && x.LaterCollectedEvents == 1);
        Assert.All(report.Acquisition.Where(x => x.Unit == "physical-sources"), x => { Assert.Null(x.Entered); Assert.Null(x.Passed); });
        string json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("private-", json);
        Assert.DoesNotContain("StartByteOffset", json);

        var unknown = late with { CapturedAtUtc = late.ObservedAtUtc };
        var withUnknownRole = ForecastThresholdStudy.ExplainEligibility(data with { Tokens = [unknown] });
        Assert.Contains(withUnknownRole.Origins, x => x.OriginReasonOverlaps.ContainsKey("unknown-role-token-sessions"));
    }

    [Fact]
    public void WaterfallAndLabelsKeepStudyDenominatorsAndMissingModelEffortDoNotBlockUnconstrainedRequests()
    {
        CodexForecastDataset data = Data();
        data = data with
        {
            Workload = [Meta("root", "cli")],
            Tokens = Enumerable.Range(0, 40).Select(i => new CodexPredictiveTokenEvent("root", Start.AddMinutes(i * 30),
                Start.AddMinutes(i * 30), null, null, 1, 0, 0, 0, 0, 1)).ToArray(),
        };
        ForecastEligibilityReport report = ForecastThresholdStudy.ExplainEligibility(data);
        ForecastThresholdReport study = ForecastThresholdStudy.Evaluate(data);
        foreach (ForecastOriginEligibility row in report.Origins)
        {
            var score = study.Scores.Single(x => x.Family == "scenario" && x.Configuration == "current" &&
                x.Cohort == row.Cohort && x.Target == row.Target && x.Period == row.Period);
            Assert.Equal(score.CandidateOrigins, row.CandidateOrigins);
            Assert.Equal(score.PointOrigins, row.Stages.Single(x => x.Stage == "point-answer").Passed);
            Assert.Equal(score.PairedOutcomes, row.Stages.Single(x => x.Stage == "paired-outcome").Passed);
            Assert.Equal(score.BandOrigins, row.Stages.Single(x => x.Stage == "uncertainty-band").Passed);
            Assert.Equal(0, row.OriginWorkloadRejected);
            Assert.All(row.Stages, x => Assert.InRange(x.Passed!.Value, 0, x.Entered!.Value));
            Assert.All(row.TrainingSelection, x => Assert.Equal("history-interval-origin-pairs", x.Unit));
        }
        Assert.Contains(report.Origins, row => row.Stages.Any(x => x.Stage == "point-answer" && x.Passed > 0));
        Assert.Contains(report.Origins, row => row.Stages.Single(x => x.Stage == "observed-outcome").Passed < row.CandidateOrigins);
    }

    [Fact]
    public void ScenarioSelectionMatchesOriginalPolicyIncludingLatestCohortTiesAndBoundaryDates()
    {
        var random = new Random(947);
        ScenarioHistorySample[] history = Enumerable.Range(0, 160).Select(i =>
        {
            DateTimeOffset start = Start.AddHours(-random.Next(0, 900));
            return new ScenarioHistorySample(i % 2 == 0 ? QuotaWindowKind.Weekly : QuotaWindowKind.FiveHour,
                start, start.AddHours(i % 11 == 0 ? -1 : 1), i % 17 == 0 ? double.NaN : i % 105,
                i % 13 == 0 ? -1 : i % 4, i % 3, i % 3 == 0 ? null : "model", i % 4 == 0 ? null : "effort",
                Start.AddDays(1), i % 3 == 0 ? "source-a" : "source-b", i % 5 == 0 ? null : "account");
        }).ToArray();
        foreach (string? account in new[] { null, "account", "absent" })
        foreach (string? model in new[] { null, "model", "absent" })
        foreach (QuotaWindowKind kind in new[] { QuotaWindowKind.FiveHour, QuotaWindowKind.Weekly })
        {
            var request = new ScenarioRequest(1, 1, 0, Model: model, ReasoningEffort: "effort", AccountKey: account);
            ScenarioHistorySample[] original = history.Where(x => x.Kind == kind && x.AccountKey == request.AccountKey &&
                x.EndUtc <= Start && x.EndUtc > x.StartUtc && double.IsFinite(x.QuotaDeltaPercent) &&
                x.QuotaDeltaPercent is >= 0 and <= 100 && x.RootAgents >= 0 && x.Subagents >= 0 &&
                (request.Cohorts.Count == 0 || x.Cohort is not null && request.Cohorts.Contains(x.Cohort)) &&
                (x.ResetUtc is null || x.EndUtc <= x.ResetUtc)).OrderBy(x => x.StartUtc).ToArray();
            bool stale = original.Length > 0 && Start - original.Max(x => x.EndUtc) > TimeSpan.FromDays(30);
            if (!stale)
            {
                var latest = original.MaxBy(x => x.EndUtc);
                original = original.Where(x => x.Source == latest?.Source && x.AccountKey == latest?.AccountKey && x.Cohort == latest?.Cohort)
                    .Where(x => x.EndUtc >= Start.AddDays(-30) && (string.IsNullOrWhiteSpace(request.Model) || x.DominantModel == request.Model) &&
                        (string.IsNullOrWhiteSpace(request.ReasoningEffort) || x.DominantReasoningEffort == request.ReasoningEffort)).ToArray();
            }
            ScenarioHistorySelection actual = ScenarioHistoryEligibility.Select(request, history, kind, Start);
            Assert.Equal(stale, actual.IsStale);
            Assert.Equal(original, actual.Samples);
        }
    }

    [Fact]
    public void IntervalReasonsOverlapAndRequestedModelEffortNeverBorrowUnknownLabels()
    {
        var row = new ScenarioHistorySample(QuotaWindowKind.Weekly, Start.AddHours(-1), Start, 1, 1, 0, null, null,
            AccountKey: "account");
        var bad = row with { Kind = QuotaWindowKind.FiveHour, AccountKey = "other", EndUtc = Start.AddHours(1), QuotaDeltaPercent = -1 };
        var request = new ScenarioRequest(1, 1, 0, Model: "model", ReasoningEffort: "effort", AccountKey: "account");
        ScenarioHistorySelection result = ScenarioHistoryEligibility.Select(request, [row, bad], QuotaWindowKind.Weekly, Start);
        var first = result.Stages[0];
        Assert.Equal(2, first.Entered);
        Assert.Equal(1, first.Passed);
        Assert.Equal(4, first.Reasons.Values.Sum());
        var last = result.Stages[^1];
        Assert.Equal(1, last.Entered);
        Assert.Equal(0, last.Passed);
        Assert.Equal(1, last.Reasons["requested-model-unmatched"]);
        Assert.Equal(1, last.Reasons["requested-effort-unmatched"]);
        Assert.Contains("0 match the requested time/model/effort", QuotaPredictionService.Simulate(request, [row, bad], Start).Weekly.Explanation);
        Assert.Single(ScenarioHistoryEligibility.Select(request with { Model = null, ReasoningEffort = null }, [row, bad],
            QuotaWindowKind.Weekly, Start).Samples);
    }

    [Fact]
    public void RequestedShapeReportsEveryUnsupportedCoordinateAndDoesNotChangeUnknownRoleTokenQuantity()
    {
        var history = new[] { new ScenarioHistorySample(QuotaWindowKind.Weekly, Start, Start.AddHours(.5), 1, 1, 0, null, null) };
        Assert.Equal(new[] { "unsupported-intensity", "duration-outside-support", "root-sessions-outside-support", "subagent-sessions-outside-support" },
            ScenarioHistoryEligibility.RequestSupportReasons(new ScenarioRequest(2, 3, 2, 2), history));
        var data = Data() with { Tokens = [new("unknown-role", Start, Start, null, null, 7, 0, 0, 0, 0, 7)] };
        var features = CodexForecastFeatureBuilder.Build(data, Start, availability: ForecastReplayAvailability.CollectedByOrigin);
        Assert.Equal(7, features.Tokens);
        Assert.Equal(new[] { "no-classified-token-sessions", "unknown-role-token-sessions" }, ScenarioHistoryEligibility.OriginReasons(features));
    }
}
