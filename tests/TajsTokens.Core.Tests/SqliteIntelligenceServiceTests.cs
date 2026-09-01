using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class SqliteIntelligenceServiceTests
{
    [Fact]
    public async Task Query_OnFreshDatabase_InitializesBaseTelemetrySchema()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-intelligence-fresh-");
        var database = Path.Combine(directory.FullName, "telemetry.db");

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            var now = DateTimeOffset.UtcNow;

            var dashboard = await intelligence.QueryAsync(
                new IntelligenceQuery(now.AddHours(-1), now, AnalyticsBucketSize.Hour, 24),
                CancellationToken.None);

            Assert.Empty(dashboard.UsageHistory);
            Assert.Empty(dashboard.QuotaBurnIntervals);
            Assert.Empty(dashboard.ResetEvents);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RefreshAndQuery_PersistForecastsDetectResetAndCorrelateBurnIntervals()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-intelligence-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var observedNow = DateTimeOffset.UtcNow;
        var start = observedNow.AddHours(-8);

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var observatory = new SqliteCodexObservatoryStore(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);
            await observatory.InitializeAsync(CancellationToken.None);

            await observatory.UpsertSessionAsync(
                new CodexSession("root", null, "fixture-repo", start, start.AddHours(2), "completed"),
                CancellationToken.None);
            await observatory.UpsertSessionAsync(
                new CodexSession("child", null, "fixture-repo", start.AddMinutes(4), start.AddMinutes(50), "completed"),
                CancellationToken.None);
            await observatory.UpsertAgentAsync(
                new Agent("root", "root", "Root agent", AgentRuntimeState.Completed, start.AddHours(2), "gpt-5.6-luna"),
                CancellationToken.None);
            await observatory.UpsertAgentAsync(
                new Agent("child", "child", "Worker", AgentRuntimeState.Completed, start.AddMinutes(50), "gpt-5.6-luna"),
                CancellationToken.None);
            await observatory.UpsertAgentRelationshipAsync(
                new AgentRelationship("root", "child", start.AddMinutes(4)),
                CancellationToken.None);

            await observatory.ApplyCumulativeTokenObservationAsync(
                Tokens("root-1", "root", start.AddMinutes(5), 1_000, 800, 100, 1_100),
                CancellationToken.None);
            await observatory.ApplyCumulativeTokenObservationAsync(
                Tokens("child-1", "child", start.AddMinutes(7), 600, 450, 80, 680),
                CancellationToken.None);
            await observatory.ApplyCumulativeTokenObservationAsync(
                Tokens("child-2", "child", start.AddMinutes(20), 1_400, 1_000, 160, 1_560),
                CancellationToken.None);
            await observatory.ApplyCumulativeTokenObservationAsync(
                Tokens("root-2", "root", start.AddMinutes(25), 3_000, 2_300, 300, 3_300),
                CancellationToken.None);

            var fiveHourReset = start.AddHours(5);
            await repository.UpsertQuotaSnapshotAsync(Quota(QuotaWindowKind.FiveHour, start, 10, fiveHourReset), CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(Quota(QuotaWindowKind.FiveHour, start.AddMinutes(10), 15, fiveHourReset, "rollout"), CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(Quota(QuotaWindowKind.FiveHour, start.AddMinutes(30), 28, fiveHourReset, "rollout"), CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(Quota(QuotaWindowKind.FiveHour, fiveHourReset.AddMinutes(2), 2, fiveHourReset.AddHours(5)), CancellationToken.None);

            var weeklyReset = start.AddDays(6);
            await repository.UpsertQuotaSnapshotAsync(Quota(QuotaWindowKind.Weekly, start, 40, weeklyReset, windowMinutes: 10_080), CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(Quota(QuotaWindowKind.Weekly, start.AddMinutes(30), 43, weeklyReset, "rollout", 10_080), CancellationToken.None);

            var refresh = await intelligence.RefreshAsync(CancellationToken.None);
            Assert.True(refresh.ForecastsPersisted >= 2);
            Assert.True(refresh.ResetEventsDetected >= 1);

            var dashboard = await intelligence.QueryAsync(
                new IntelligenceQuery(start.AddMinutes(-1), observedNow.AddMinutes(1), AnalyticsBucketSize.Minute, 720),
                CancellationToken.None);

            Assert.NotEmpty(dashboard.UsageHistory);
            Assert.Contains(dashboard.Dimensions, item => item.Dimension == "Repository" && item.Value == "fixture-repo");
            Assert.Contains(dashboard.Dimensions, item => item.Dimension == "Agent role" && item.Value == "Root");
            Assert.Contains(dashboard.Dimensions, item => item.Dimension == "Agent role" && item.Value == "Subagent");
            Assert.NotEmpty(dashboard.Heatmap);
            Assert.Contains(dashboard.ResetEvents, item => item.Classification == QuotaResetClassification.ExpectedReset);
            Assert.NotEmpty(dashboard.FiveHourForecasts);
            Assert.NotEmpty(dashboard.WeeklyForecasts);

            var burn = dashboard.QuotaBurnIntervals.First(interval =>
                interval.Kind == QuotaWindowKind.FiveHour && interval.DeltaUsedPercent >= 10);
            Assert.True(burn.NativeTokens > 0);
            Assert.True(burn.RootTokens > 0);
            Assert.True(burn.SubagentTokens > 0);

            var detail = await intelligence.GetQuotaBurnDetailAsync(burn, 20, CancellationToken.None);
            Assert.Contains(detail.Contributors, item => item.SessionId == "root" && !item.IsSubagent);
            Assert.Contains(detail.Contributors, item => item.SessionId == "child" && item.IsSubagent);
            Assert.Contains("Estimated attribution", detail.Methodology, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Query_DoesNotCreateBurnIntervalWhenResetIdentityIsUnknown()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-intelligence-reset-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var now = DateTimeOffset.UtcNow;

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);

            await repository.UpsertQuotaSnapshotAsync(QuotaWithoutReset(QuotaWindowKind.FiveHour, now.AddMinutes(-20), 10), CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(QuotaWithoutReset(QuotaWindowKind.FiveHour, now.AddMinutes(-10), 25), CancellationToken.None);

            var dashboard = await intelligence.QueryAsync(
                new IntelligenceQuery(now.AddHours(-1), now, AnalyticsBucketSize.Hour, 24),
                CancellationToken.None);

            Assert.Empty(dashboard.QuotaBurnIntervals);
            Assert.All(dashboard.UsageHistory, bucket => Assert.Null(bucket.FiveHourQuotaDelta));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static CodexCumulativeTokenObservation Tokens(
        string eventId,
        string session,
        DateTimeOffset observed,
        long input,
        long cached,
        long output,
        long total) =>
        new(
            eventId,
            $"{session}.jsonl",
            session,
            session,
            observed,
            "gpt-5.6-luna",
            "xhigh",
            input,
            cached,
            0,
            output,
            output / 2,
            total);

    private static QuotaSnapshot Quota(
        QuotaWindowKind kind,
        DateTimeOffset captured,
        double used,
        DateTimeOffset reset,
        string source = "app-server",
        int? windowMinutes = null) =>
        new(
            kind,
            captured,
            used,
            windowMinutes ?? (kind == QuotaWindowKind.FiveHour ? 300 : 10_080),
            reset,
            "codex",
            "default",
            source);

    private static QuotaSnapshot QuotaWithoutReset(
        QuotaWindowKind kind,
        DateTimeOffset captured,
        double used) =>
        new(
            kind,
            captured,
            used,
            kind == QuotaWindowKind.FiveHour ? 300 : 10_080,
            null,
            "codex",
            "default",
            "fixture");
}
