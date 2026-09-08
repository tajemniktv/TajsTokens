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
            var currentWeekly = Quota(QuotaWindowKind.Weekly, start.AddMinutes(31), 43, weeklyReset, "app-server", 10_080);
            await repository.UpsertQuotaSnapshotAsync(currentWeekly, CancellationToken.None);
            var currentFiveHour = Quota(QuotaWindowKind.FiveHour, fiveHourReset.AddMinutes(2), 2, fiveHourReset.AddHours(5));

            var currentForecasts = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [
                    new QuotaLaneState(QuotaWindowKind.FiveHour, "codex", "default", currentFiveHour, TelemetryHealthState.Live, currentFiveHour.CapturedAtUtc),
                    new QuotaLaneState(QuotaWindowKind.Weekly, "codex", "default", currentWeekly, TelemetryHealthState.Live, currentWeekly.CapturedAtUtc)
                ],
                observedNow,
                CancellationToken.None);
            Assert.Equal(2, currentForecasts.Count);
            Assert.All(currentForecasts, item => Assert.NotNull(item.Forecast));

            var refresh = await intelligence.RefreshAsync(CancellationToken.None);
            Assert.Equal(0, refresh.ForecastsPersisted);
            Assert.True(refresh.ResetEventsDetected >= 1);

            var dashboard = await intelligence.QueryAsync(
                new IntelligenceQuery(start.AddMinutes(-1), observedNow.AddMinutes(1), AnalyticsBucketSize.Minute, 720),
                CancellationToken.None);

            Assert.NotEmpty(dashboard.UsageHistory);
            Assert.Contains(dashboard.Dimensions, item => item.Dimension == "Session repository" && item.Value == "fixture-repo");
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

    [Fact]
    public async Task Query_HoldsOneSnapshotWhileWalAllowsConcurrentNativeCommit()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-intelligence-snapshot-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var observed = DateTimeOffset.UtcNow.AddMinutes(-5);
        var queryPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var store = new SqliteCodexObservatoryStore(database);
            await repository.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(
                new CodexCumulativeTokenObservation("first", "session.jsonl", "session", "session", observed, "model-a", "high", 100, 0, 0, 10, 0, 110),
                CancellationToken.None);
            var intelligence = new SqliteIntelligenceService(database, repository, async (stage, token) =>
            {
                if (stage == "usage-loaded")
                {
                    queryPaused.TrySetResult();
                    await resumeQuery.Task.WaitAsync(token);
                }
            });
            var queryTask = intelligence.QueryAsync(
                new IntelligenceQuery(observed.AddHours(-1), observed.AddHours(1), AnalyticsBucketSize.Hour, 24),
                CancellationToken.None);
            await queryPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var writer = store.ApplyCumulativeTokenObservationAsync(
                new CodexCumulativeTokenObservation("second", "session.jsonl", "session", "session", observed.AddMinutes(1), "model-b", "high", 200, 0, 0, 20, 0, 220),
                CancellationToken.None);
            var completed = await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(writer, completed);
            await writer;
            resumeQuery.TrySetResult();

            var dashboard = await queryTask;
            Assert.Equal(110, Assert.Single(dashboard.UsageHistory).NativeTokens);
            var models = dashboard.Dimensions.Where(item => item.Dimension == "Model").ToArray();
            Assert.Single(models);
            Assert.Equal("model-a", models[0].Value);
            Assert.Equal(110, models[0].NativeTokens);
        }
        finally
        {
            resumeQuery.TrySetResult();
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CurrentForecast_StaleAuthoritativeLaneDoesNotBorrowPersistedHistory()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-current-forecast-stale-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var now = DateTimeOffset.UtcNow;
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);
            var reset = now.AddHours(4);
            var previous = Quota(QuotaWindowKind.FiveHour, now.AddHours(-1), 10, reset, "codex-app-server:codex");
            var stale = Quota(QuotaWindowKind.FiveHour, now.AddMinutes(-5), 20, reset, "codex-app-server:codex");
            await repository.UpsertQuotaSnapshotAsync(previous, CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(stale, CancellationToken.None);

            var results = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [new QuotaLaneState(QuotaWindowKind.FiveHour, "codex", "default", stale, TelemetryHealthState.Stale, stale.CapturedAtUtc)],
                now, CancellationToken.None);
            var generation = Assert.Single(results);
            Assert.Equal(TelemetryHealthState.Stale, generation.State);
            Assert.Null(generation.Forecast);
            Assert.Contains("paused", generation.HistoryPolicy, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await repository.GetRecentForecastSnapshotsAsync(
                QuotaWindowKind.FiveHour, "codex", "default", 10, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CurrentForecast_DoesNotBridgePreviousResetEpoch()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-current-forecast-reset-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var now = DateTimeOffset.UtcNow;
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);
            var oldReset = now.AddHours(1);
            var currentReset = now.AddHours(6);
            await repository.UpsertQuotaSnapshotAsync(Quota(QuotaWindowKind.FiveHour, now.AddHours(-2), 10, oldReset, "codex-rollout:primary"), CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(Quota(QuotaWindowKind.FiveHour, now.AddHours(-1), 80, oldReset, "codex-app-server:codex"), CancellationToken.None);
            var current = Quota(QuotaWindowKind.FiveHour, now.AddMinutes(-5), 5, currentReset, "codex-app-server:codex");
            await repository.UpsertQuotaSnapshotAsync(current, CancellationToken.None);

            var results = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [new QuotaLaneState(QuotaWindowKind.FiveHour, "codex", "default", current, TelemetryHealthState.Live, current.CapturedAtUtc)],
                now, CancellationToken.None);
            var generation = Assert.Single(results);
            Assert.NotNull(generation.Forecast);
            Assert.Equal(ForecastState.Learning, generation.Forecast!.State);
            Assert.Null(generation.Forecast.BurnRatePercentPerHour);
            Assert.Equal(current.CapturedAtUtc, generation.Current.CapturedAtUtc);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CurrentForecast_UsesProviderAnchorAndIgnoresNewerEmbeddedObservation()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-current-forecast-anchor-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        var reset = start.AddHours(5);
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);
            var first = Quota(QuotaWindowKind.FiveHour, start, 10, reset, "codex-app-server:codex");
            var current = Quota(QuotaWindowKind.FiveHour, start.AddHours(1), 20, reset, "codex-app-server:codex");
            var newerEmbedded = Quota(QuotaWindowKind.FiveHour, start.AddHours(1).AddMinutes(5), 90, reset, "codex-rollout:primary");
            await repository.UpsertQuotaSnapshotAsync(first, CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(current, CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(newerEmbedded, CancellationToken.None);

            var results = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [new QuotaLaneState(QuotaWindowKind.FiveHour, "codex", "default", current, TelemetryHealthState.Live, current.CapturedAtUtc)],
                newerEmbedded.CapturedAtUtc.AddMinutes(5),
                CancellationToken.None);
            var generation = Assert.Single(results);
            Assert.Equal(current, generation.Current);
            Assert.Equal(QuotaObservationAuthority.ProviderAuthoritative, generation.Authority);
            Assert.NotNull(generation.Forecast);
            Assert.Equal(10d, generation.Forecast!.BurnRatePercentPerHour);

            var persisted = await repository.GetRecentForecastSnapshotsAsync(
                QuotaWindowKind.FiveHour, "codex", "default", 10, CancellationToken.None);
            var saved = Assert.Single(persisted);
            Assert.Equal(current.Source, saved.QuotaSource);
            Assert.Equal(QuotaObservationAuthority.ProviderAuthoritative, saved.QuotaAuthority);
            Assert.Equal(current.CapturedAtUtc, saved.QuotaCapturedAtUtc);
            Assert.Equal(current.WindowMinutes, saved.QuotaWindowMinutes);
            Assert.Equal(current.ResetsAtUtc, saved.QuotaResetsAtUtc);
            Assert.Equal(generation.Forecast, saved.Forecast);

            var dashboard = await intelligence.QueryAsync(
                new IntelligenceQuery(start.AddHours(-1), newerEmbedded.CapturedAtUtc.AddHours(1), AnalyticsBucketSize.Hour, 24),
                CancellationToken.None);
            var savedThroughDashboard = Assert.Single(dashboard.FiveHourForecasts);
            Assert.Equal(saved, savedThroughDashboard);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CurrentForecast_PreservesEqualTimestampAuthoritiesRegardlessWriteOrder(bool providerFirst)
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-current-forecast-authority-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var captured = DateTimeOffset.Parse("2026-08-31T10:00:00Z");
        var reset = captured.AddHours(5);
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);

            var previous = Quota(QuotaWindowKind.FiveHour, captured.AddHours(-1), 10, reset, "codex-app-server:codex");
            var current = Quota(QuotaWindowKind.FiveHour, captured, 20, reset, "codex-app-server:codex");
            var rollout = Quota(QuotaWindowKind.FiveHour, captured, 99, reset, "codex-rollout:primary");
            await repository.UpsertQuotaSnapshotAsync(previous, CancellationToken.None);
            if (providerFirst)
            {
                await repository.UpsertQuotaSnapshotAsync(current, CancellationToken.None);
                await repository.UpsertQuotaSnapshotAsync(rollout, CancellationToken.None);
            }
            else
            {
                await repository.UpsertQuotaSnapshotAsync(rollout, CancellationToken.None);
                await repository.UpsertQuotaSnapshotAsync(current, CancellationToken.None);
            }

            var snapshots = await repository.GetRecentQuotaSnapshotsAsync(
                QuotaWindowKind.FiveHour, "codex", "default", 10, CancellationToken.None);
            Assert.Equal(3, snapshots.Count);

            var results = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [new QuotaLaneState(QuotaWindowKind.FiveHour, "codex", "default", current, TelemetryHealthState.Live, current.CapturedAtUtc)],
                captured.AddHours(1),
                CancellationToken.None);
            var generation = Assert.Single(results);
            Assert.NotNull(generation.Forecast);
            Assert.Equal(10d, generation.Forecast!.BurnRatePercentPerHour);

            var persisted = Assert.Single(await repository.GetRecentForecastSnapshotsAsync(
                QuotaWindowKind.FiveHour, "codex", "default", 10, CancellationToken.None));
            Assert.Equal(QuotaObservationAuthority.ProviderAuthoritative, persisted.QuotaAuthority);
            Assert.Equal(current.Source, persisted.QuotaSource);
            Assert.Equal(current.WindowMinutes, persisted.QuotaWindowMinutes);
            Assert.Equal(current.ResetsAtUtc, persisted.QuotaResetsAtUtc);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CurrentForecast_BoundsQuotaHistoryBeforeSqlLimit()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-current-forecast-history-bound-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var anchor = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var reset = anchor.AddDays(2);
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(
                Quota(QuotaWindowKind.FiveHour, anchor.AddHours(-1), 10, reset, "codex-app-server:codex"),
                CancellationToken.None);
            var current = Quota(QuotaWindowKind.FiveHour, anchor, 20, reset, "codex-app-server:codex");
            await repository.UpsertQuotaSnapshotAsync(current, CancellationToken.None);

            for (var index = 1; index <= 520; index++)
            {
                await repository.UpsertQuotaSnapshotAsync(
                    Quota(
                        QuotaWindowKind.FiveHour,
                        anchor.AddMinutes(index),
                        20 + index * 0.01,
                        reset,
                        $"codex-rollout:{index}"),
                    CancellationToken.None);
            }

            var results = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [new QuotaLaneState(QuotaWindowKind.FiveHour, "codex", "default", current, TelemetryHealthState.Live, current.CapturedAtUtc)],
                anchor.AddMinutes(10),
                CancellationToken.None);
            var generation = Assert.Single(results);
            Assert.NotNull(generation.Forecast);
            Assert.Equal(10d, generation.Forecast!.BurnRatePercentPerHour);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CurrentForecast_FutureAnchorWithOlderHistoryReturnsNoForecastAndDoesNotPersist()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-current-forecast-future-history-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var now = DateTimeOffset.Parse("2026-08-31T12:00:00Z");
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);
            var current = Quota(QuotaWindowKind.FiveHour, now.AddMinutes(10), 20, now.AddHours(5), "codex-app-server:codex");
            await repository.UpsertQuotaSnapshotAsync(
                Quota(QuotaWindowKind.FiveHour, now.AddHours(-1), 10, now.AddHours(5), "codex-app-server:codex"),
                CancellationToken.None);

            var results = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [new QuotaLaneState(QuotaWindowKind.FiveHour, "codex", "default", current, TelemetryHealthState.Live, current.CapturedAtUtc)],
                now,
                CancellationToken.None);
            var generation = Assert.Single(results);
            Assert.Null(generation.Forecast);
            Assert.Equal(TelemetryHealthState.Live, generation.State);
            Assert.Contains("newer", generation.Diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await repository.GetRecentForecastSnapshotsAsync(
                QuotaWindowKind.FiveHour, "codex", "default", 10, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CurrentForecast_FutureAnchorWithoutHistoryReturnsNoForecastAndDoesNotPersist()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-current-forecast-future-empty-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var now = DateTimeOffset.Parse("2026-08-31T12:00:00Z");
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);
            var current = Quota(QuotaWindowKind.FiveHour, now.AddMinutes(10), 20, now.AddHours(5), "codex-app-server:codex");

            var results = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [new QuotaLaneState(QuotaWindowKind.FiveHour, "codex", "default", current, TelemetryHealthState.Live, current.CapturedAtUtc)],
                now,
                CancellationToken.None);
            var generation = Assert.Single(results);
            Assert.Null(generation.Forecast);
            Assert.Equal(TelemetryHealthState.Live, generation.State);
            Assert.Contains("newer", generation.Diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await repository.GetRecentForecastSnapshotsAsync(
                QuotaWindowKind.FiveHour, "codex", "default", 10, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task NativeAndUsage_KeepMissingEventModelUnknownDespiteLaterAgentModel()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-model-attribution-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var observed = DateTimeOffset.UtcNow.AddMinutes(-10);
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var store = new SqliteCodexObservatoryStore(database);
            await repository.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);
            await store.UpsertAgentAsync(new Agent("session-a", "session-a", "Agent", AgentRuntimeState.Running, observed.AddMinutes(1), "later-model"), CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(
                new CodexCumulativeTokenObservation("unknown-model", "session-a.jsonl", "session-a", "session-a", observed, null, "high", 100, 20, 0, 10, 2, 115),
                CancellationToken.None);
            var native = await new TajsTokens.Infrastructure.Providers.SqliteNativeCodexAccountingProvider(database).GetSnapshotAsync(CancellationToken.None);
            var dashboard = await new SqliteIntelligenceService(database, repository).QueryAsync(
                new IntelligenceQuery(observed.AddHours(-1), observed.AddHours(1), AnalyticsBucketSize.Hour, 24), CancellationToken.None);
            Assert.Equal("(unknown)", Assert.Single(native.Usage).Model);
            var model = Assert.Single(dashboard.Dimensions, item => item.Dimension == "Model");
            Assert.Equal("(unknown)", model.Value);
            Assert.Equal(115, model.NativeTokens);
            Assert.Equal(5, model.IntegrityDelta);
            Assert.False(model.IntegrityExact);
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
