using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class EvidenceAnswerScopeTests
{
    [Fact]
    public async Task UnknownMetadataRetainsAllTimeAmountsButNeverManufacturesDatesOrCurrentAccountOwnership()
    {
        using var f = new CalibrationEvidenceTests.Fixture();
        await f.InitializeAsync();
        await f.Store.UpsertRolloutFileAsync("source", "scope.jsonl", "s", 20, f.Now, default);
        var dated = new CodexCumulativeTokenObservation("dated", "scope.jsonl", "s", null, f.Now.AddMinutes(-5),
            null, null, 100, 0, 0, 0, 0, 100) { CapturedAtUtc = f.Now.AddMinutes(-4) };
        var missing = new CodexCumulativeTokenObservation("missing", "scope.jsonl", "s", null, f.Now,
            null, null, 200, 0, 0, 0, 0, 200)
        {
            CapturedAtUtc = f.Now, SourceEventAtUtc = null, SourceTimestampQuality = CodexSourceTimestampQuality.Missing,
        };
        await f.Store.ApplyCumulativeTokenObservationAsync(dated, default);
        await f.Store.ApplyCumulativeTokenObservationAsync(missing, default);
        await f.Store.ApplyCumulativeTokenObservationAsync(dated, default);
        await f.Store.ApplyCumulativeTokenObservationAsync(missing, default);
        await f.Repository.UpsertQuotaSnapshotAsync(f.Quota with { AccountKey = "current-login" }, default);

        CodexObservatorySummary summary = await f.Store.GetSummaryAsync(default);
        Assert.Equal(200, summary.NativeTokens.ReportedTotal);
        Assert.Equal(new CodexNativeTokenCoverage(2, 1, 100, 1, 100, 2, 200), summary.TokenCoverage);
        var native = await new SqliteNativeCodexAccountingProvider(f.Path).GetSnapshotAsync(default);
        Assert.Equal(200, Assert.Single(native.Usage).Breakdown.Total);
        Assert.Null(Assert.Single(native.Usage).SourceEventAtUtc);
        Assert.Equal(100, Assert.Single(native.Hourly).Breakdown.Total);

        var reader = new SqliteForecastDatasetReader(f.Path);
        CodexForecastDataset data = await reader.ReadAsync("codex", "default", f.Now.AddHours(-1), f.Now.AddMinutes(1),
            default, accountKey: "current-login", includeLedger: true);
        Assert.Single(data.Tokens);
        var entry = Assert.Single(data.Ledger);
        Assert.Equal(100, entry.Workload.ReportedTotalTokens);
        Assert.Null(entry.Workload.Model);
        Assert.Null(entry.Workload.ReasoningEffort);
        Assert.Null(entry.AccountKey);
        Assert.Equal(AccountEvidenceClass.Unattributed, entry.Ownership);
        Assert.Empty(CodexIntelligenceProjection.Select(data.Ledger,
            new CodexSelection(f.Now.AddHours(-1), f.Now.AddMinutes(1), AccountKey: "current-login")));
        var features = CodexForecastFeatureBuilder.Build(data, f.Now, availability: ForecastReplayAvailability.CollectedByOrigin);
        Assert.Equal(100, features.Tokens);
        Assert.Equal(1, features.TokenActiveUnknownSessions);

        var daily = await new SqliteCodexDailyReportReader(f.Path).ReadAsync(f.Now.AddDays(-1), f.Now.AddDays(1), default);
        Assert.Equal(100, Assert.Single(daily.Days).ReportedTotal);
        Assert.Equal(summary.TokenCoverage, daily.AllTimeCoverage);
        var recent = await new SqliteCodexObservatoryReadModel(f.Path).GetRecentSessionTokensAsync(f.Now.AddMinutes(-15), ["s"], default, f.Now);
        Assert.Equal(100, recent["s"].ReportedTotal);
    }

    [Fact]
    public void MissingRoleModelEffortAndAccountDoNotDisableDirectTokenOrQuotaPaceAnswers()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var tokens = Enumerable.Range(0, 140).Select(i => new CodexPredictiveTokenEvent("unknown-role", start.AddMinutes(i * 30),
            start.AddMinutes(i * 30), null, null, 1000, 0, 0, 0, 0, 1000)).ToArray();
        DateTimeOffset now = tokens[^1].ObservedAtUtc.AddMinutes(2);
        QuotaSnapshot[] quota = Enumerable.Range(0, 13).Select(i => new QuotaSnapshot(QuotaWindowKind.Weekly,
            now.AddMinutes(-120 + i * 10), 10 + i, 10080, now.AddDays(3), "codex", "default", "codex-app-server:codex", "meter-account")
        { CollectedAtUtc = now.AddMinutes(-120 + i * 10), HasSourceTimestamp = true }).ToArray();
        var data = new CodexForecastDataset(quota, [], tokens, [], now, "local fixture");
        Assert.Equal(1, CodexForecastFeatureBuilder.Build(data, now).TokenActiveUnknownSessions);
        TokenWorkloadForecast forecast = TokenWorkloadPredictionService.Predict(data, now);
        Assert.NotEmpty(forecast.Predictions);
        Assert.All(forecast.Predictions, x => Assert.True(double.IsFinite(x.ExpectedTokens) && x.ExpectedTokens >= 0));
        Assert.Equal(tokens.Sum(x => x.ReportedTotalTokens), QuotaEvaluationCoverageBuilder.Build(data).ReportedTokens);
        var outlook = QuotaPredictionService.Predict(data, quota[^1], now);
        Assert.NotEmpty(outlook);
        Assert.All(outlook, x => Assert.InRange(x.RemainingPercent, 0, 100));
        var noLocalMetadata = QuotaPredictionService.BuildResetOutlook(quota, now);
        Assert.NotEqual(ForecastState.Learning, noLocalMetadata.State);
    }

    [Fact]
    public async Task FleetRecentTokensUseVerifiedSourceTimeAndExplicitUpperBound()
    {
        using var f = new CalibrationEvidenceTests.Fixture();
        await f.InitializeAsync();
        foreach (var row in new[]
        {
            ("recent", -5, CodexSourceTimestampQuality.Valid), ("old", -60, CodexSourceTimestampQuality.Valid),
            ("future", 5, CodexSourceTimestampQuality.Valid), ("legacy", -5, CodexSourceTimestampQuality.LegacyUnverified),
            ("missing", -5, CodexSourceTimestampQuality.Missing),
        })
            await f.Store.ApplyCumulativeTokenObservationAsync(new CodexCumulativeTokenObservation(row.Item1, row.Item1 + ".jsonl", "s-" + row.Item1, null,
                f.Now, null, null, 100, 0, 0, 0, 0, 100)
            { SourceEventAtUtc = f.Now.AddMinutes(row.Item2), SourceTimestampQuality = row.Item3, CapturedAtUtc = f.Now }, default);
        Assert.Equal(500, (await f.Store.GetSummaryAsync(default)).NativeTokens.ReportedTotal);
        var recent = await new SqliteCodexObservatoryReadModel(f.Path).GetRecentSessionTokensAsync(f.Now.AddMinutes(-15),
            ["s-recent", "s-old", "s-future", "s-legacy", "s-missing"], default, f.Now);
        Assert.Equal("s-recent", Assert.Single(recent).Key);
        Assert.Equal(100, recent["s-recent"].ReportedTotal);
    }
}
