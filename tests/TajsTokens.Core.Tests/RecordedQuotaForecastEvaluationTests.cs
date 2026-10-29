using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class RecordedQuotaForecastEvaluationTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-10-29T00:00:00Z");
    private static QuotaSnapshot Q(double minutes, double? used, string plan = "pro") => new(QuotaWindowKind.Weekly,
        Origin.AddMinutes(minutes), used, 10080, Origin.AddHours(3), "codex", "default", "codex-app-server:codex", "private-account")
        { LimitId = "private-limit", PlanType = plan, HasSourceTimestamp = true, CollectedAtUtc = Origin.AddMinutes(minutes).AddSeconds(2) };
    private static QuotaForecastComputedPoint Point(double remaining = 78, double minutes = 5) => new(
        QuotaForecastPointRole.DirectBaseline, Origin.AddMinutes(minutes), minutes / 60, "time-ewma-2h", remaining,
        80 - remaining, null, null, 0, 0, 0, false, ForecastSupportReason.None);
    private static QuotaForecastComputationEntry Entry(params QuotaForecastComputedPoint[] points) => new("fixture",
        Origin.AddSeconds(4), new(QuotaForecastComputationPhase.Detailed, QuotaHistoryPolicy.Cohort(Q(0, 20)), Origin, Origin,
            Origin.AddSeconds(2), "private-observation", 20, Origin.AddHours(3), Origin.AddSeconds(3), "fixture-policy", new('0', 64), null,
            points.Length == 0 ? [Point()] : points), null, Origin.AddSeconds(5));
    private static RecordedQuotaForecastReport Evaluate(QuotaForecastComputationEntry entry, params QuotaSnapshot[] rows) =>
        RecordedQuotaForecastEvaluation.Evaluate([entry], rows, Origin.AddHours(4), includeTrials: true);

    [Fact]
    public void ScoresFrozenOutputsAtActualRecordedTargetsAndPairsOnlyIdenticalOutcomes()
    {
        var direct = Point() with { LowerRemainingPercent = 75, UpperRemainingPercent = 79 };
        var selected = direct with { Role = QuotaForecastPointRole.SelectedHorizon, Model = "selected", RemainingPercent = 77 };
        var report = Evaluate(Entry(direct, selected), Q(0, 20), Q(5 + 40d / 60, 24));
        var score = Assert.Single(report.Scores, s => s.Role == "DirectBaseline");
        Assert.Equal(2, score.MeanSignedRemainingError); Assert.Equal(2, score.MeanAbsoluteRemainingError);
        Assert.Equal(2, score.NonOverlappingSignedErrorSum); Assert.Equal(1, score.IntervalCoverage); Assert.Equal(4, score.MeanIntervalWidth);
        var comparison = Assert.Single(report.Comparisons);
        Assert.Equal(1, comparison.SelectedMeanAbsoluteError); Assert.Equal(2, comparison.DirectMeanAbsoluteError);
        Assert.All(report.Trials, t => Assert.Equal("matched", t.OutcomeState));
        Assert.Equal(78, report.Trials[0].PredictedRemaining); // Never extrapolate it to the delayed label time.
        string exported = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("private-account", exported); Assert.DoesNotContain("private-limit", exported); Assert.DoesNotContain("private-observation", exported);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("decrease")]
    [InlineData("invalid")]
    [InlineData("conflict")]
    [InlineData("reset-chain")]
    public void NeverBridgesRegimeInvalidConflictDecreaseOrResetBoundaries(string boundary)
    {
        var middle = boundary switch
        {
            "plan" => Q(2, 21, "other"), "decrease" => Q(2, 19), "invalid" => Q(2, null),
            "reset-chain" => Q(2, 21) with { ResetsAtUtc = Origin.AddHours(3).AddSeconds(1) }, _ => Q(2, 21),
        };
        var end = boundary == "reset-chain" ? Q(5, 24) with { ResetsAtUtc = Origin.AddHours(3).AddSeconds(2) } : Q(5, 24);
        QuotaSnapshot[] rows = boundary == "conflict" ? [Q(0, 20), middle, Q(2, 22), end] : [Q(0, 20), middle, end];
        var trial = Assert.Single(Evaluate(Entry(), rows).Trials);
        Assert.Equal("compatible-target-unavailable", trial.OutcomeState);
        Assert.Null(trial.ObservedRemaining);
    }

    [Fact]
    public void EqualCopiesAddNoBoundaryOrOutcomeSupport()
    {
        var report = Evaluate(Entry(), Q(0, 20), Q(2, 21), Q(2, 21), Q(5, 24), Q(5, 24));
        Assert.Equal(1, Assert.Single(report.Scores).MatchedTargets);
    }

    [Fact]
    public void CollectionToleranceAndCutoffNeverInventOrSubstituteALabel()
    {
        var entry = Entry();
        Assert.Equal("outcome-collection-unknown", Assert.Single(Evaluate(entry, Q(0, 20), Q(5, 24) with { CollectedAtUtc = null }).Trials).OutcomeState);
        Assert.Equal("invalid-outcome-clock", Assert.Single(Evaluate(entry, Q(0, 20), Q(5, 24) with { CollectedAtUtc = Origin.AddMinutes(4) }).Trials).OutcomeState);
        Assert.Equal("outcome-collected-too-late", Assert.Single(Evaluate(entry, Q(0, 20), Q(5, 24) with { CollectedAtUtc = Origin.AddMinutes(11) }, Q(5.5, 25)).Trials).OutcomeState);
        Assert.Equal("compatible-target-unavailable", Assert.Single(Evaluate(entry, Q(0, 20), Q(6.01, 24)).Trials).OutcomeState);
        var cutoff = RecordedQuotaForecastEvaluation.Evaluate([entry], [Q(0, 20), Q(5, 24) with { CollectedAtUtc = Origin.AddMinutes(10) }], Origin.AddMinutes(6), includeTrials: true);
        Assert.Equal("compatible-target-unavailable", Assert.Single(cutoff.Trials).OutcomeState);
        var immature = RecordedQuotaForecastEvaluation.Evaluate([entry], [Q(0, 20)], Origin.AddMinutes(4), includeTrials: true);
        Assert.Equal("target-immature", Assert.Single(immature.Trials).OutcomeState);
    }

    [Fact]
    public void LateUnconfirmedAndFutureIssueClocksCannotEnterProspectiveScores()
    {
        var e = Entry();
        Assert.Equal("issued-at-or-after-target", Assert.Single(Evaluate(e with { ConfirmedAtUtc = Origin.AddMinutes(5) }, Q(0, 20), Q(5, 24)).Trials).OutcomeState);
        Assert.Equal("unconfirmed", Assert.Single(Evaluate(e with { ConfirmedAtUtc = null }, Q(0, 20), Q(5, 24)).Trials).OutcomeState);
        Assert.Equal("invalid-issue-clock", Assert.Single(Evaluate(e with { Computation = e.Computation! with { GeneratedAtUtc = Origin.AddMinutes(1) } }, Q(0, 20), Q(5, 24)).Trials).OutcomeState);
        var future = RecordedQuotaForecastEvaluation.Evaluate([e with { ConfirmedAtUtc = Origin.AddMinutes(2) }], [Q(0, 20)], Origin.AddMinutes(1), includeTrials: true);
        Assert.Equal("confirmation-after-cutoff", Assert.Single(future.Trials).OutcomeState);
    }

    [Fact]
    public void CensoredTargetsRemainCountedButAreNotExactOrdinaryLossOrLatentCost()
    {
        var report = Evaluate(Entry(Point(0)), Q(0, 20), Q(5, 100));
        var score = Assert.Single(report.Scores);
        Assert.Equal(1, score.MatchedTargets); Assert.Equal(1, score.SaturatedTargets); Assert.Equal(0, score.OrdinaryLossOutcomes);
        Assert.Null(score.MeanAbsoluteRemainingError); Assert.Equal(1, score.ThresholdComparisons);
        Assert.Equal(5 - 5d / 60, score.MeanWarningToSaturationReportMinutes!.Value, 8);
        var delayed = Assert.Single(Evaluate(Entry(Point(0)), Q(0, 20), Q(5.5, 100)).Scores);
        Assert.Equal(1, delayed.SaturatedTargets); Assert.Equal(0, delayed.ThresholdComparisons); Assert.Null(delayed.MeanWarningToSaturationReportMinutes);
    }

    [Fact]
    public void PreResetProxyCannotEstablishNegativeThresholdLabelAndLeadUsesConfirmation()
    {
        var resetPoint = Point(0, 180) with { Role = QuotaForecastPointRole.SelectedReset, SurvivesUntilReset = false };
        var e = Entry(resetPoint);
        var proxy = Assert.Single(Evaluate(e, Q(0, 20), Q(179, 50)).Scores);
        Assert.Equal(1, proxy.OrdinaryLossOutcomes); Assert.Equal(0, proxy.ThresholdComparisons);
        var exact = Assert.Single(Evaluate(e, Q(0, 20), Q(180, 50)).Scores);
        Assert.Equal(1, exact.FalseThresholdPredictions);
        var actual = Assert.Single(Evaluate(e with { ConfirmedAtUtc = Origin.AddMinutes(2) }, Q(0, 20), Q(1, 100), Q(179, 100)).Scores);
        Assert.Equal(-1, actual.MeanWarningToSaturationReportMinutes); // A delayed computation was not an early warning.
    }

    [Fact]
    public void OverlappingOrRepeatedComputationsCannotInflateAccumulatedErrorSupport()
    {
        var entries = Enumerable.Range(0, 6).Select(i =>
        {
            var e = Entry(Point(78 + -i, 5 + i));
            return e with { Id = "entry-" + i, RecordedAtUtc = Origin.AddMinutes(i).AddSeconds(4), ConfirmedAtUtc = Origin.AddMinutes(i).AddSeconds(5),
                Computation = e.Computation! with { AnchorUtc = Origin.AddMinutes(i), AnchorMeasuredUtc = Origin.AddMinutes(i),
                    AnchorCollectedUtc = Origin.AddMinutes(i).AddSeconds(2), GeneratedAtUtc = Origin.AddMinutes(i).AddSeconds(3), AnchorUsedPercent = 20 + i,
                    Points = [Point(78 - i, 5 + i) with { HorizonHours = 5d / 60 }] } };
        }).ToArray();
        var quota = Enumerable.Range(0, 11).Select(i => Q(i, 20 + i)).ToArray();
        var report = RecordedQuotaForecastEvaluation.Evaluate(entries, quota, Origin.AddHours(4));
        var score = Assert.Single(report.Scores);
        Assert.Equal(6, score.OrdinaryLossOutcomes); Assert.Equal(2, score.NonOverlappingOutcomes);
        Assert.Equal(6, score.NonOverlappingSignedErrorSum);
    }

    [Fact]
    public void MissingOtherAccountAndRevisedAnchorAreNotZeroOutcomes()
    {
        Assert.Equal("compatible-target-unavailable", Assert.Single(Evaluate(Entry(), Q(0, 20), Q(5, 24) with { AccountKey = "other" }).Trials).OutcomeState);
        Assert.Equal("anchor-unavailable-or-revised", Assert.Single(Evaluate(Entry(), Q(0, 21), Q(5, 24)).Trials).OutcomeState);
        var exhausted = Entry() with { Computation = Entry().Computation! with { AnchorUsedPercent = 100 } };
        Assert.Equal("anchor-not-forecastable", Assert.Single(Evaluate(exhausted, Q(0, 100), Q(5, 100)).Trials).OutcomeState);
    }

    [Fact]
    public void PointBandAndBeforeTargetAvailabilityAreSeparateFromOutcomeAvailability()
    {
        var entry = Entry(Point() with { LowerRemainingPercent = 75, UpperRemainingPercent = 79 });
        var immature = Assert.Single(RecordedQuotaForecastEvaluation.Evaluate([entry], [Q(0, 20)], Origin.AddMinutes(1)).Scores);
        Assert.Equal(1, immature.SupportedPoints); Assert.Equal(1, immature.BeforeTargetSupportedPoints);
        Assert.Equal(1, immature.BandComputations); Assert.Equal(1, immature.BeforeTargetBandComputations);
        Assert.Equal(0, immature.BandedOutcomes); Assert.Null(immature.IntervalCoverage);
        var late = Assert.Single(Evaluate(entry with { ConfirmedAtUtc = Origin.AddMinutes(5) }, Q(0, 20), Q(5, 24)).Scores);
        Assert.Equal(1, late.SupportedPoints); Assert.Equal(0, late.BeforeTargetSupportedPoints);
        Assert.Equal(1, late.BandComputations); Assert.Equal(0, late.BeforeTargetBandComputations);
    }

    [Fact]
    public async Task QuotaOnlyReadWorksWithoutAnObservatoryStore()
    {
        string directory = Directory.CreateTempSubdirectory("tajstokens-quota-only-score-").FullName;
        try
        {
            string database = Path.Combine(directory, "telemetry.db");
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeIntelligenceAsync(default);
            await repository.UpsertQuotaSnapshotAsync(Q(0, 20), default);
            await repository.UpsertQuotaSnapshotAsync(Q(5, 24), default);
            await repository.UpsertQuotaSnapshotAsync(Q(6, 25) with { SourceIdentity = "unverified-native-membership" }, default);
            var data = await new SqliteForecastDatasetReader(database).ReadQuotaOnlyAsync("codex", "default", Origin.AddMinutes(-1), Origin.AddHours(1), default, null);
            Assert.Equal(2, data.Quota.Count);
            Assert.Equal("matched", Assert.Single(RecordedQuotaForecastEvaluation.Evaluate([Entry()], data.Quota, Origin.AddHours(1), includeTrials: true).Trials).OutcomeState);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task QuotaOnlyReadKeepsOriginalClocksAndDoesNotDependOnWorkloadOrTokenTables()
    {
        string directory = Directory.CreateTempSubdirectory("tajstokens-recorded-score-").FullName;
        try
        {
            string database = Path.Combine(directory, "telemetry.db");
            var repository = new SqliteTelemetryRepository(database);
            using var store = new SqliteCodexObservatoryStore(database);
            await repository.InitializeIntelligenceAsync(default); await store.InitializeAsync(default);
            await repository.UpsertQuotaSnapshotAsync(Q(0, 20), default);
            await repository.UpsertQuotaSnapshotAsync(Q(5, 24), default);
            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP TABLE codex_workload_observations;
                    CREATE VIEW codex_workload_observations AS SELECT * FROM unavailable_training_source;
                    DROP TABLE codex_native_token_events;
                    CREATE VIEW codex_native_token_events AS SELECT * FROM unavailable_token_source;
                    """;
                await command.ExecuteNonQueryAsync();
            }
            var data = await new SqliteForecastDatasetReader(database).ReadQuotaOnlyAsync("codex", "default", Origin.AddMinutes(-1), Origin.AddHours(1), default, null);
            Assert.Equal(2, data.Quota.Count); Assert.Equal(Origin, data.Quota[0].CapturedAtUtc);
            Assert.Equal(Origin.AddSeconds(2), data.Quota[0].CollectedAtUtc);
            Assert.Empty(data.Workload); Assert.Empty(data.Tokens); Assert.Empty(data.Context); Assert.Empty(data.Ledger);
            Assert.Equal("matched", Assert.Single(RecordedQuotaForecastEvaluation.Evaluate([Entry()], data.Quota, Origin.AddHours(1), includeTrials: true).Trials).OutcomeState);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public void FutureRowsAndLaterCapturesCannotImproveAnEarlierCutoff()
    {
        var e = Entry();
        var asOf = Origin.AddMinutes(6);
        var before = RecordedQuotaForecastEvaluation.Evaluate([e], [Q(0, 20), Q(5, 24)], asOf, includeTrials: true);
        var after = RecordedQuotaForecastEvaluation.Evaluate([e, e with { RecordedAtUtc = Origin.AddMinutes(7), ConfirmedAtUtc = Origin.AddMinutes(7) }],
            [Q(0, 20), Q(5, 24), Q(5, 30) with { CollectedAtUtc = Origin.AddMinutes(7) }, Q(10, 50)], asOf, includeTrials: true);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
    }

    [Fact]
    public void MatchedFalseThresholdAndMissedDisplayedBurstAreDescriptiveCounts()
    {
        var falseThreshold = Assert.Single(Evaluate(Entry(Point(0)), Q(0, 20), Q(5, 21)).Scores);
        Assert.Equal(1, falseThreshold.FalseThresholdPredictions);
        var missedBurst = Assert.Single(Evaluate(Entry(Point(80)), Q(0, 20), Q(5, 23)).Scores);
        Assert.Equal(1, missedBurst.MissedDisplayedBursts);
    }
}
