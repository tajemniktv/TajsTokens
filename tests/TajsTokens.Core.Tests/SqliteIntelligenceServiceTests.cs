// Taj's Tokens | SqliteIntelligenceServiceTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;
using TajsTokens.Infrastructure.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class SqliteIntelligenceServiceTests
{
    [Fact]
    public async Task UsageReport_PartialBucketsAndFilteredDimensionsReconcileWithoutTopTwentyTruncation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PROJECT.md"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Repository root is required for project-local test files.");
        string directory = Path.Combine(root.FullName, ".codex", "temp", "usage-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string database = Path.Combine(directory, "telemetry.db");
            var repository = new SqliteTelemetryRepository(database);
            var store = new SqliteCodexObservatoryStore(database);
            await repository.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);
            DateTimeOffset start = DateTimeOffset.Parse("2026-09-08T12:30:00Z");
            for (int i = 0; i < 25; i++)
            {
                string id = "session-" + i;
                await store.UpsertSessionAsync(
                    new CodexSession(
                        id,
                        i == 0 ? "different-thread-id" : null,
                        i % 2 == 0 ? "project-a" : "project-b",
                        start,
                        start,
                        "completed"),
                    CancellationToken.None);
                await store.ApplyCumulativeTokenObservationAsync(
                    Tokens(id, id, start.AddMinutes(1), 100, 60, 20, 120),
                    CancellationToken.None);
            }
            var service = new SqliteIntelligenceService(database, repository);
            var query = new IntelligenceQuery(start, start.AddHours(1)) { UsageOnly = true };
            IntelligenceDashboard retained = await service.QueryAsync(
                query with { FromUtc = DateTimeOffset.UnixEpoch },
                CancellationToken.None);
            Assert.Equal(DateTimeOffset.UnixEpoch, retained.Query.FromUtc);
            Assert.Equal(AnalyticsBucketSize.Month, retained.Query.BucketSize);
            Assert.Equal(3000, retained.UsageHistory.Sum(x => x.NativeTokens));
            foreach (AnalyticsBucketSize size in new[] { AnalyticsBucketSize.Hour, AnalyticsBucketSize.Day, AnalyticsBucketSize.Month })
            {
                IntelligenceDashboard all = await service.QueryAsync(query with { BucketSize = size }, CancellationToken.None);
                Assert.Equal(3000, Assert.Single(all.UsageHistory).NativeTokens);
                Assert.Equal(25, all.Dimensions.Count(x => x.Dimension == "Session"));
                Assert.Equal(3000, all.Dimensions.Where(x => x.Dimension == "Session").Sum(x => x.NativeTokens));
                Assert.All(all.Dimensions, x => Assert.True(x.IntegrityExact));
                Assert.Empty(all.QuotaBurnIntervals);
            }
            IntelligenceDashboard project = await service.QueryAsync(query with { Repository = "project-a" }, CancellationToken.None);
            Assert.Equal(1560, Assert.Single(project.UsageHistory).NativeTokens);
            Assert.Equal(1560, project.Heatmap.Sum(x => x.NativeTokens));
            Assert.Equal(13, project.Dimensions.Count(x => x.Dimension == "Session"));
            IntelligenceDashboard session = await service.QueryAsync(
                query with { Repository = "project-a", SessionId = "session-0", Model = "gpt-5.6-luna" },
                CancellationToken.None);
            Assert.Equal(120, Assert.Single(session.UsageHistory).NativeTokens);
            Assert.Equal("different-thread-id", Assert.Single(session.Dimensions, x => x.Dimension == "Session").ThreadId);
            IntelligenceDashboard thread = await service.QueryAsync(
                query with { ThreadId = "different-thread-id" },
                CancellationToken.None);
            Assert.Equal(120, Assert.Single(thread.UsageHistory).NativeTokens);
            Assert.Equal(120, thread.Heatmap.Sum(x => x.NativeTokens));
            Assert.Empty((await service.QueryAsync(query with { ThreadId = "session-0" }, CancellationToken.None)).UsageHistory);
            Assert.Null(Assert.Single(project.Dimensions, x => x.Dimension == "Session" && x.Value == "session-2").ThreadId);
            IntelligenceDashboard excluded = await service.QueryAsync(
                query with { Repository = "project-b", SessionId = "session-0" },
                CancellationToken.None);
            Assert.Empty(excluded.UsageHistory);
            Assert.Empty(excluded.Dimensions);
            Assert.Empty(excluded.Heatmap);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task BurnIntervals_NeverBridgeSources_AndFiltersApplyToIntervals()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-burn-sources-");
        try
        {
            string database = Path.Combine(directory.FullName, "telemetry.db");
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(CancellationToken.None);
            DateTimeOffset start = DateTimeOffset.UtcNow.AddHours(-1);
            DateTimeOffset reset = start.AddHours(5);
            foreach (QuotaSnapshot row in new[]
                     {
                         Quota(QuotaWindowKind.FiveHour, start, 10, reset, "codex-app-server:codex"),
                         Quota(QuotaWindowKind.FiveHour, start.AddMinutes(5), 50, reset, "codex-rollout:primary"),
                         Quota(QuotaWindowKind.FiveHour, start.AddMinutes(10), 12, reset, "codex-app-server:codex"),
                         Quota(QuotaWindowKind.FiveHour, start.AddMinutes(15), 53, reset, "codex-rollout:primary"),
                     })
                await repository.UpsertQuotaSnapshotAsync(row, CancellationToken.None);
            var service = new SqliteIntelligenceService(database, repository);
            var query = new IntelligenceQuery(start.AddMinutes(-1), start.AddMinutes(30));
            IntelligenceDashboard all = await service.QueryAsync(query, CancellationToken.None);
            Assert.Equal(2, all.QuotaBurnIntervals.Count);
            Assert.All(all.QuotaBurnIntervals, x => Assert.Equal(x.BeforeSource, x.AfterSource));
            Assert.Equal(new[] { 2d, 3d }, all.QuotaBurnIntervals.Select(x => x.DeltaUsedPercent).Order().ToArray());
            IntelligenceDashboard account = await service.QueryAsync(
                query with { BurnAuthority = QuotaObservationAuthority.ProviderAuthoritative },
                CancellationToken.None);
            QuotaBurnInterval selected = Assert.Single(account.QuotaBurnIntervals);
            Assert.Equal(2, selected.DeltaUsedPercent);
            QuotaBurnDetail detail = await service.GetQuotaBurnDetailAsync(selected, 10, CancellationToken.None);
            Assert.Equal(selected.IntervalId, detail.Interval.IntervalId);
            IntelligenceDashboard weekly = await service.QueryAsync(
                query with { BurnKind = QuotaWindowKind.Weekly },
                CancellationToken.None);
            Assert.Empty(weekly.QuotaBurnIntervals);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Query_OnFreshDatabase_InitializesBaseTelemetrySchema()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-intelligence-fresh-");
        string database = Path.Combine(directory.FullName, "telemetry.db");

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            IntelligenceDashboard dashboard = await intelligence.QueryAsync(
                new IntelligenceQuery(now.AddHours(-1), now, AnalyticsBucketSize.Hour, 24),
                CancellationToken.None);

            Assert.Empty(dashboard.UsageHistory);
            Assert.Empty(dashboard.QuotaBurnIntervals);
            Assert.Empty(dashboard.ResetEvents);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task RefreshAndQuery_PersistForecastsDetectResetAndCorrelateBurnIntervals()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-intelligence-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        DateTimeOffset observedNow = DateTimeOffset.UtcNow;
        DateTimeOffset start = observedNow.AddHours(-8);

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var observatory = new SqliteCodexObservatoryStore(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            // Exercise incremental detection, not the one-time historical cache rebuild.
            await repository.InitializeIntelligenceAsync(CancellationToken.None);
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

            DateTimeOffset fiveHourReset = start.AddHours(5);
            await repository.UpsertQuotaSnapshotAsync(Quota(QuotaWindowKind.FiveHour, start, 10, fiveHourReset), CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(
                Quota(QuotaWindowKind.FiveHour, start.AddMinutes(10), 15, fiveHourReset, "rollout"),
                CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(
                Quota(QuotaWindowKind.FiveHour, start.AddMinutes(30), 28, fiveHourReset, "rollout"),
                CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(
                Quota(QuotaWindowKind.FiveHour, fiveHourReset.AddMinutes(2), 2, fiveHourReset.AddHours(5)),
                CancellationToken.None);

            DateTimeOffset weeklyReset = start.AddDays(6);
            await repository.UpsertQuotaSnapshotAsync(
                Quota(QuotaWindowKind.Weekly, start, 40, weeklyReset, windowMinutes: 10_080),
                CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(
                Quota(QuotaWindowKind.Weekly, start.AddMinutes(30), 43, weeklyReset, "rollout", 10_080),
                CancellationToken.None);
            QuotaSnapshot currentWeekly = Quota(QuotaWindowKind.Weekly, start.AddMinutes(31), 43, weeklyReset, "app-server", 10_080);
            await repository.UpsertQuotaSnapshotAsync(currentWeekly, CancellationToken.None);
            QuotaSnapshot currentFiveHour = Quota(QuotaWindowKind.FiveHour, fiveHourReset.AddMinutes(2), 2, fiveHourReset.AddHours(5));

            IReadOnlyList<CurrentQuotaForecast> currentForecasts = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [
                    new QuotaLaneState(
                        QuotaWindowKind.FiveHour,
                        "codex",
                        "default",
                        currentFiveHour,
                        TelemetryHealthState.Live,
                        currentFiveHour.CapturedAtUtc),
                    new QuotaLaneState(
                        QuotaWindowKind.Weekly,
                        "codex",
                        "default",
                        currentWeekly,
                        TelemetryHealthState.Live,
                        currentWeekly.CapturedAtUtc),
                ],
                observedNow,
                CancellationToken.None);
            Assert.Equal(2, currentForecasts.Count);
            Assert.All(currentForecasts, item => Assert.NotNull(item.Forecast));

            IntelligenceRefreshResult refresh = await intelligence.RefreshAsync(CancellationToken.None);
            Assert.Equal(0, refresh.ForecastsPersisted);
            Assert.True(refresh.ResetEventsDetected >= 1);

            IntelligenceDashboard dashboard = await intelligence.QueryAsync(
                new IntelligenceQuery(start.AddMinutes(-1), observedNow.AddMinutes(1), AnalyticsBucketSize.Minute),
                CancellationToken.None);

            Assert.NotEmpty(dashboard.UsageHistory);
            Assert.Contains(dashboard.Dimensions, item => item.Dimension == "Session repository" && item.Value == "fixture-repo");
            Assert.Contains(dashboard.Dimensions, item => item.Dimension == "Agent role" && item.Value == "Root");
            Assert.Contains(dashboard.Dimensions, item => item.Dimension == "Agent role" && item.Value == "Subagent");
            Assert.NotEmpty(dashboard.Heatmap);
            Assert.Contains(dashboard.ResetEvents, item => item.Classification == QuotaResetClassification.ExpectedReset);
            Assert.NotEmpty(dashboard.FiveHourForecasts);
            Assert.NotEmpty(dashboard.WeeklyForecasts);

            QuotaBurnInterval burn = dashboard.QuotaBurnIntervals.First(interval =>
                interval.Kind == QuotaWindowKind.FiveHour && interval.DeltaUsedPercent >= 10);
            Assert.True(burn.NativeTokens > 0);
            Assert.True(burn.RootTokens > 0);
            Assert.True(burn.SubagentTokens > 0);

            QuotaBurnDetail detail = await intelligence.GetQuotaBurnDetailAsync(burn, 20, CancellationToken.None);
            Assert.Contains(detail.Contributors, item => item.SessionId == "root" && !item.IsSubagent);
            Assert.Contains(detail.Contributors, item => item.SessionId == "child" && item.IsSubagent);
            Assert.Contains("not verified to belong to that account", detail.Methodology, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Query_DoesNotCreateBurnIntervalWhenResetIdentityIsUnknown()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-intelligence-reset-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);

            await repository.UpsertQuotaSnapshotAsync(
                QuotaWithoutReset(QuotaWindowKind.FiveHour, now.AddMinutes(-20), 10),
                CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(
                QuotaWithoutReset(QuotaWindowKind.FiveHour, now.AddMinutes(-10), 25),
                CancellationToken.None);

            IntelligenceDashboard dashboard = await intelligence.QueryAsync(
                new IntelligenceQuery(now.AddHours(-1), now, AnalyticsBucketSize.Hour, 24),
                CancellationToken.None);

            Assert.Empty(dashboard.QuotaBurnIntervals);
            Assert.All(dashboard.UsageHistory, bucket => Assert.Null(bucket.FiveHourQuotaDelta));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Query_HoldsOneSnapshotWhileWalAllowsConcurrentNativeCommit()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-intelligence-snapshot-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        DateTimeOffset observed = DateTimeOffset.UtcNow.AddMinutes(-5);
        var queryPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var store = new SqliteCodexObservatoryStore(database);
            await repository.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(
                new CodexCumulativeTokenObservation(
                    "first",
                    "session.jsonl",
                    "session",
                    "session",
                    observed,
                    "model-a",
                    "high",
                    100,
                    0,
                    0,
                    10,
                    0,
                    110),
                CancellationToken.None);
            var intelligence = new SqliteIntelligenceService(
                database,
                repository,
                async (stage, token) =>
                {
                    if (stage == "usage-loaded")
                    {
                        queryPaused.TrySetResult();
                        await resumeQuery.Task.WaitAsync(token);
                    }
                });
            Task<IntelligenceDashboard> queryTask = intelligence.QueryAsync(
                new IntelligenceQuery(observed.AddHours(-1), observed.AddHours(1), AnalyticsBucketSize.Hour, 24),
                CancellationToken.None);
            await queryPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task writer = store.ApplyCumulativeTokenObservationAsync(
                new CodexCumulativeTokenObservation(
                    "second",
                    "session.jsonl",
                    "session",
                    "session",
                    observed.AddMinutes(1),
                    "model-b",
                    "high",
                    200,
                    0,
                    0,
                    20,
                    0,
                    220),
                CancellationToken.None);
            Task completed = await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(writer, completed);
            await writer;
            resumeQuery.TrySetResult();

            IntelligenceDashboard dashboard = await queryTask;
            Assert.Equal(110, Assert.Single(dashboard.UsageHistory).NativeTokens);
            UsageDimensionTotal[] models = dashboard.Dimensions.Where(item => item.Dimension == "Model").ToArray();
            Assert.Single(models);
            Assert.Equal("model-a", models[0].Value);
            Assert.Equal(110, models[0].NativeTokens);
        }
        finally
        {
            resumeQuery.TrySetResult();
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CurrentForecast_StaleAuthoritativeLaneDoesNotBorrowPersistedHistory()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-current-forecast-stale-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);
            DateTimeOffset reset = now.AddHours(4);
            QuotaSnapshot previous = Quota(QuotaWindowKind.FiveHour, now.AddHours(-1), 10, reset, "codex-app-server:codex");
            QuotaSnapshot stale = Quota(QuotaWindowKind.FiveHour, now.AddMinutes(-5), 20, reset, "codex-app-server:codex");
            await repository.UpsertQuotaSnapshotAsync(previous, CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(stale, CancellationToken.None);

            IReadOnlyList<CurrentQuotaForecast> results = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [new QuotaLaneState(QuotaWindowKind.FiveHour, "codex", "default", stale, TelemetryHealthState.Stale, stale.CapturedAtUtc)],
                now,
                CancellationToken.None);
            CurrentQuotaForecast generation = Assert.Single(results);
            Assert.Equal(TelemetryHealthState.Stale, generation.State);
            Assert.Null(generation.Forecast);
            Assert.Contains("paused", generation.HistoryPolicy, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(
                await repository.GetRecentForecastSnapshotsAsync(
                    QuotaWindowKind.FiveHour,
                    "codex",
                    "default",
                    10,
                    CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CurrentForecast_DoesNotBridgePreviousResetEpoch()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-current-forecast-reset-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);
            DateTimeOffset oldReset = now.AddHours(1);
            DateTimeOffset currentReset = now.AddHours(6);
            await repository.UpsertQuotaSnapshotAsync(
                Quota(QuotaWindowKind.FiveHour, now.AddHours(-2), 10, oldReset, "codex-rollout:primary"),
                CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(
                Quota(QuotaWindowKind.FiveHour, now.AddHours(-1), 80, oldReset, "codex-app-server:codex"),
                CancellationToken.None);
            QuotaSnapshot current = Quota(QuotaWindowKind.FiveHour, now.AddMinutes(-5), 5, currentReset, "codex-app-server:codex");
            await repository.UpsertQuotaSnapshotAsync(current, CancellationToken.None);

            IReadOnlyList<CurrentQuotaForecast> results = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [
                    new QuotaLaneState(
                        QuotaWindowKind.FiveHour,
                        "codex",
                        "default",
                        current,
                        TelemetryHealthState.Live,
                        current.CapturedAtUtc),
                ],
                now,
                CancellationToken.None);
            CurrentQuotaForecast generation = Assert.Single(results);
            Assert.NotNull(generation.Forecast);
            Assert.Equal(ForecastState.Learning, generation.Forecast!.State);
            Assert.Null(generation.Forecast.BurnRatePercentPerHour);
            Assert.Equal(current.CapturedAtUtc, generation.Current.CapturedAtUtc);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CurrentForecast_UsesProviderAnchorAndIgnoresNewerEmbeddedObservation()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-current-forecast-anchor-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        DateTimeOffset start = DateTimeOffset.UtcNow.AddHours(-2);
        DateTimeOffset reset = start.AddHours(5);
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);
            QuotaSnapshot first = Quota(QuotaWindowKind.FiveHour, start, 10, reset, "codex-app-server:codex");
            QuotaSnapshot current = Quota(QuotaWindowKind.FiveHour, start.AddHours(1), 20, reset, "codex-app-server:codex");
            QuotaSnapshot newerEmbedded = Quota(
                QuotaWindowKind.FiveHour,
                start.AddHours(1).AddMinutes(5),
                90,
                reset,
                "codex-rollout:primary");
            await repository.UpsertQuotaSnapshotAsync(first, CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(current, CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(newerEmbedded, CancellationToken.None);

            IReadOnlyList<CurrentQuotaForecast> results = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [
                    new QuotaLaneState(
                        QuotaWindowKind.FiveHour,
                        "codex",
                        "default",
                        current,
                        TelemetryHealthState.Live,
                        current.CapturedAtUtc),
                ],
                newerEmbedded.CapturedAtUtc.AddMinutes(5),
                CancellationToken.None);
            CurrentQuotaForecast generation = Assert.Single(results);
            Assert.Equal(current, generation.Current);
            Assert.Equal(QuotaObservationAuthority.ProviderAuthoritative, generation.Authority);
            Assert.NotNull(generation.Forecast);
            Assert.Equal(10d, generation.Forecast!.BurnRatePercentPerHour);

            IReadOnlyList<ForecastSnapshot> persisted = await repository.GetRecentForecastSnapshotsAsync(
                QuotaWindowKind.FiveHour,
                "codex",
                "default",
                10,
                CancellationToken.None);
            ForecastSnapshot saved = Assert.Single(persisted);
            Assert.Equal(current.Source, saved.QuotaSource);
            Assert.Equal(QuotaObservationAuthority.ProviderAuthoritative, saved.QuotaAuthority);
            Assert.Equal(current.CapturedAtUtc, saved.QuotaCapturedAtUtc);
            Assert.Equal(current.WindowMinutes, saved.QuotaWindowMinutes);
            Assert.Equal(current.ResetsAtUtc, saved.QuotaResetsAtUtc);
            Assert.Equivalent(generation.Forecast, saved.Forecast, true);
            var horizons = Assert.IsAssignableFrom<IReadOnlyList<QuotaHorizonPrediction>>(saved.Forecast.Evidence!.HorizonPredictions);
            Assert.Equal(2, horizons.Count);
            Assert.Equal(75, horizons[0].RemainingPercent, 6);
            Assert.False(horizons[0].UsesWorkload);

            IntelligenceDashboard dashboard = await intelligence.QueryAsync(
                new IntelligenceQuery(start.AddHours(-1), newerEmbedded.CapturedAtUtc.AddHours(1), AnalyticsBucketSize.Hour, 24),
                CancellationToken.None);
            ForecastSnapshot savedThroughDashboard = Assert.Single(dashboard.FiveHourForecasts);
            Assert.Equivalent(saved, savedThroughDashboard, true);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CurrentForecast_PreservesEqualTimestampAuthoritiesRegardlessWriteOrder(bool providerFirst)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-current-forecast-authority-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        DateTimeOffset captured = DateTimeOffset.Parse("2026-08-31T10:00:00Z");
        DateTimeOffset reset = captured.AddHours(5);
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);

            QuotaSnapshot previous = Quota(QuotaWindowKind.FiveHour, captured.AddHours(-1), 10, reset, "codex-app-server:codex");
            QuotaSnapshot current = Quota(QuotaWindowKind.FiveHour, captured, 20, reset, "codex-app-server:codex");
            QuotaSnapshot rollout = Quota(QuotaWindowKind.FiveHour, captured, 99, reset, "codex-rollout:primary");
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

            IReadOnlyList<QuotaSnapshot> snapshots = await repository.GetRecentQuotaSnapshotsAsync(
                QuotaWindowKind.FiveHour,
                "codex",
                "default",
                10,
                CancellationToken.None);
            Assert.Single(snapshots);
            Assert.Equal(
                2,
                (await repository.GetRecentQuotaSnapshotsAsync(
                    QuotaWindowKind.FiveHour,
                    "codex",
                    "default",
                    10,
                    CancellationToken.None,
                    accountKey: "fixture-account")).Count);

            IReadOnlyList<CurrentQuotaForecast> results = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [
                    new QuotaLaneState(
                        QuotaWindowKind.FiveHour,
                        "codex",
                        "default",
                        current,
                        TelemetryHealthState.Live,
                        current.CapturedAtUtc),
                ],
                captured.AddHours(1),
                CancellationToken.None);
            CurrentQuotaForecast generation = Assert.Single(results);
            Assert.NotNull(generation.Forecast);
            Assert.Equal(10d, generation.Forecast!.BurnRatePercentPerHour);

            ForecastSnapshot persisted = Assert.Single(
                await repository.GetRecentForecastSnapshotsAsync(
                    QuotaWindowKind.FiveHour,
                    "codex",
                    "default",
                    10,
                    CancellationToken.None));
            Assert.Equal(QuotaObservationAuthority.ProviderAuthoritative, persisted.QuotaAuthority);
            Assert.Equal(current.Source, persisted.QuotaSource);
            Assert.Equal(current.WindowMinutes, persisted.QuotaWindowMinutes);
            Assert.Equal(current.ResetsAtUtc, persisted.QuotaResetsAtUtc);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CurrentForecast_BoundsQuotaHistoryBeforeSqlLimit()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-current-forecast-history-bound-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        DateTimeOffset anchor = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        DateTimeOffset reset = anchor.AddDays(2);
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(
                Quota(QuotaWindowKind.FiveHour, anchor.AddHours(-1), 10, reset, "codex-app-server:codex"),
                CancellationToken.None);
            QuotaSnapshot current = Quota(QuotaWindowKind.FiveHour, anchor, 20, reset, "codex-app-server:codex");
            await repository.UpsertQuotaSnapshotAsync(current, CancellationToken.None);

            for (int index = 1; index <= 520; index++)
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

            IReadOnlyList<CurrentQuotaForecast> results = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [
                    new QuotaLaneState(
                        QuotaWindowKind.FiveHour,
                        "codex",
                        "default",
                        current,
                        TelemetryHealthState.Live,
                        current.CapturedAtUtc),
                ],
                anchor.AddMinutes(10),
                CancellationToken.None);
            CurrentQuotaForecast generation = Assert.Single(results);
            Assert.NotNull(generation.Forecast);
            Assert.Equal(10d, generation.Forecast!.BurnRatePercentPerHour);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CurrentForecast_FutureAnchorWithOlderHistoryReturnsNoForecastAndDoesNotPersist()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-current-forecast-future-history-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-31T12:00:00Z");
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);
            QuotaSnapshot current = Quota(QuotaWindowKind.FiveHour, now.AddMinutes(10), 20, now.AddHours(5), "codex-app-server:codex");
            await repository.UpsertQuotaSnapshotAsync(
                Quota(QuotaWindowKind.FiveHour, now.AddHours(-1), 10, now.AddHours(5), "codex-app-server:codex"),
                CancellationToken.None);

            IReadOnlyList<CurrentQuotaForecast> results = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [
                    new QuotaLaneState(
                        QuotaWindowKind.FiveHour,
                        "codex",
                        "default",
                        current,
                        TelemetryHealthState.Live,
                        current.CapturedAtUtc),
                ],
                now,
                CancellationToken.None);
            CurrentQuotaForecast generation = Assert.Single(results);
            Assert.Null(generation.Forecast);
            Assert.Equal(TelemetryHealthState.Live, generation.State);
            Assert.Contains("newer", generation.Diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(
                await repository.GetRecentForecastSnapshotsAsync(
                    QuotaWindowKind.FiveHour,
                    "codex",
                    "default",
                    10,
                    CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CurrentForecast_FutureAnchorWithoutHistoryReturnsNoForecastAndDoesNotPersist()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-current-forecast-future-empty-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-31T12:00:00Z");
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await repository.InitializeAsync(CancellationToken.None);
            QuotaSnapshot current = Quota(QuotaWindowKind.FiveHour, now.AddMinutes(10), 20, now.AddHours(5), "codex-app-server:codex");

            IReadOnlyList<CurrentQuotaForecast> results = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [
                    new QuotaLaneState(
                        QuotaWindowKind.FiveHour,
                        "codex",
                        "default",
                        current,
                        TelemetryHealthState.Live,
                        current.CapturedAtUtc),
                ],
                now,
                CancellationToken.None);
            CurrentQuotaForecast generation = Assert.Single(results);
            Assert.Null(generation.Forecast);
            Assert.Equal(TelemetryHealthState.Live, generation.State);
            Assert.Contains("newer", generation.Diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(
                await repository.GetRecentForecastSnapshotsAsync(
                    QuotaWindowKind.FiveHour,
                    "codex",
                    "default",
                    10,
                    CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task NativeAndUsage_KeepMissingEventModelUnknownDespiteLaterAgentModel()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-model-attribution-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        DateTimeOffset observed = DateTimeOffset.UtcNow.AddMinutes(-10);
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            var store = new SqliteCodexObservatoryStore(database);
            await repository.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);
            await store.UpsertAgentAsync(
                new Agent("session-a", "session-a", "Agent", AgentRuntimeState.Running, observed.AddMinutes(1), "later-model"),
                CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(
                new CodexCumulativeTokenObservation(
                    "unknown-model",
                    "session-a.jsonl",
                    "session-a",
                    "session-a",
                    observed,
                    null,
                    "high",
                    100,
                    20,
                    0,
                    10,
                    2,
                    115),
                CancellationToken.None);
            CodexTokenAccountingSnapshot native =
                await new SqliteNativeCodexAccountingProvider(database).GetSnapshotAsync(CancellationToken.None);
            IntelligenceDashboard dashboard = await new SqliteIntelligenceService(database, repository).QueryAsync(
                new IntelligenceQuery(observed.AddHours(-1), observed.AddHours(1), AnalyticsBucketSize.Hour, 24),
                CancellationToken.None);
            Assert.Equal("(unknown)", Assert.Single(native.Usage).Model);
            UsageDimensionTotal model = Assert.Single(dashboard.Dimensions, item => item.Dimension == "Model");
            Assert.Equal("(unknown)", model.Value);
            Assert.Equal(115, model.NativeTokens);
            Assert.Equal(5, model.IntegrityDelta);
            Assert.False(model.IntegrityExact);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    private static CodexCumulativeTokenObservation Tokens(
        string eventId,
        string session,
        DateTimeOffset observed,
        long input,
        long cached,
        long output,
        long total)
    {
        return new CodexCumulativeTokenObservation(
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
    }

    private static QuotaSnapshot Quota(
        QuotaWindowKind kind,
        DateTimeOffset captured,
        double used,
        DateTimeOffset reset,
        string source = "app-server",
        int? windowMinutes = null)
    {
        return new QuotaSnapshot(
            kind,
            captured,
            used,
            windowMinutes ?? (kind == QuotaWindowKind.FiveHour ? 300 : 10_080),
            reset,
            "codex",
            "default",
            source,
            source == "app-server" || source.StartsWith("codex-app-server:", StringComparison.Ordinal) ? "fixture-account" : null)
        {
            ObservationId = source.Contains("rollout", StringComparison.Ordinal) ? "fixture:" + captured.ToString("O") : null,
            SourceIdentity = source.Contains("rollout", StringComparison.Ordinal) ? "fixture-source" : null,
            SessionId = source.Contains("rollout", StringComparison.Ordinal) ? "fixture-session" : null,
            CollectedAtUtc = captured,
            HasSourceTimestamp = true,
        };
    }

    private static QuotaSnapshot QuotaWithoutReset(
        QuotaWindowKind kind,
        DateTimeOffset captured,
        double used)
    {
        return new QuotaSnapshot(
            kind,
            captured,
            used,
            kind == QuotaWindowKind.FiveHour ? 300 : 10_080,
            null,
            "codex",
            "default",
            "fixture");
    }
}