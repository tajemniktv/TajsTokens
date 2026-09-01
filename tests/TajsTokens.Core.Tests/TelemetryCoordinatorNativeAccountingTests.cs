using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class TelemetryCoordinatorNativeAccountingTests
{
    [Fact]
    public async Task Refresh_ProjectsTokenGenerationAfterObservatoryCommitsIt()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-refresh-order-");
        var databasePath = Path.Combine(directory.FullName, "telemetry.db");
        try
        {
            var store = new SqliteCodexObservatoryStore(databasePath);
            var observatory = new WritingObservatoryService(store);
            var coordinator = new TelemetryCoordinator(
                new SqliteNativeCodexAccountingProvider(databasePath),
                new EmptyQuotaProvider(),
                new SqliteTelemetryRepository(databasePath),
                observatory);

            var snapshot = await coordinator.RefreshAsync(RefreshTrigger.Manual, CancellationToken.None);

            Assert.True(observatory.Completed);
            Assert.True(snapshot.TokenDataFresh);
            Assert.Equal(42, Assert.Single(snapshot.TokenUsages).Breakdown.Total);
            Assert.Equal(42, Assert.Single(snapshot.HourlyBuckets).Breakdown.Total);
            Assert.Contains(snapshot.Sources, source => source.Provider == "Native Codex" && source.State == TelemetryHealthState.Live);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ObservatoryErrorCount_PreservesLastCompleteTokenGenerationAndMarksItStale()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-refresh-errors-");
        try
        {
            var observatory = new SequencedObservatoryService(
                _ => Task.FromResult(Result(errors: 0)),
                _ => Task.FromResult(Result(errors: 1)));
            var tokens = new SequencedTokenProvider(42, 99);
            var coordinator = new TelemetryCoordinator(
                tokens,
                new EmptyQuotaProvider(),
                new SqliteTelemetryRepository(Path.Combine(directory.FullName, "telemetry.db")),
                observatory);

            var first = await coordinator.RefreshAsync(RefreshTrigger.Startup, CancellationToken.None);
            var second = await coordinator.RefreshAsync(RefreshTrigger.Interval, CancellationToken.None);

            Assert.True(first.TokenDataFresh);
            Assert.Equal(42, Assert.Single(first.TokenUsages).Breakdown.Total);
            Assert.False(second.TokenDataFresh);
            Assert.Equal(42, Assert.Single(second.TokenUsages).Breakdown.Total);
            Assert.Equal(2, tokens.Calls);
            Assert.Contains(second.Sources, source => source.Provider == "Codex rollouts" && source.State == TelemetryHealthState.Stale);
            Assert.Contains(second.Sources, source => source.Provider == "Native Codex" && source.State == TelemetryHealthState.Stale);
            Assert.Contains(second.Events, item => item.Type == "Token generation stale");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ObservatoryException_PreservesLastCompleteTokenGenerationAndMarksItStale()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-refresh-exception-");
        try
        {
            var observatory = new SequencedObservatoryService(
                _ => Task.FromResult(Result(errors: 0)),
                _ => Task.FromException<CodexObservatoryRefreshResult>(new InvalidOperationException("rollout failed")));
            var tokens = new SequencedTokenProvider(42, 123);
            var coordinator = new TelemetryCoordinator(
                tokens,
                new EmptyQuotaProvider(),
                new SqliteTelemetryRepository(Path.Combine(directory.FullName, "telemetry.db")),
                observatory);

            await coordinator.RefreshAsync(RefreshTrigger.Startup, CancellationToken.None);
            var second = await coordinator.RefreshAsync(RefreshTrigger.Interval, CancellationToken.None);

            Assert.False(second.TokenDataFresh);
            Assert.Equal(42, Assert.Single(second.TokenUsages).Breakdown.Total);
            Assert.Equal(2, tokens.Calls);
            Assert.Contains(second.Sources, source =>
                source.Provider == "Codex rollouts" &&
                source.State == TelemetryHealthState.Stale &&
                source.Detail.Contains("rollout failed", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(second.Sources, source => source.Provider == "Native Codex" && source.State == TelemetryHealthState.Stale);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ZeroDiscoveredRollouts_PreservesHistoryButCannotBeLive()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-refresh-empty-source-");
        try
        {
            var observatory = new SequencedObservatoryService(
                _ => Task.FromResult(Result(errors: 0)),
                _ => Task.FromResult(Result(errors: 0, filesDiscovered: 0)));
            var tokens = new SequencedTokenProvider(42, 99);
            var coordinator = new TelemetryCoordinator(
                tokens,
                new EmptyQuotaProvider(),
                new SqliteTelemetryRepository(Path.Combine(directory.FullName, "telemetry.db")),
                observatory);

            await coordinator.RefreshAsync(RefreshTrigger.Startup, CancellationToken.None);
            var second = await coordinator.RefreshAsync(RefreshTrigger.Interval, CancellationToken.None);

            Assert.False(second.TokenDataFresh);
            Assert.Equal(42, Assert.Single(second.TokenUsages).Breakdown.Total);
            Assert.Equal(TelemetryHealthState.Stale, second.TokenGeneration!.State);
            Assert.Contains(second.Sources, source =>
                source.Provider == "Codex rollouts" && source.State == TelemetryHealthState.Unavailable);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FreshFallback_RemainsFreshButCarriesFallbackQuality()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-token-fallback-quality-");
        try
        {
            var coordinator = new TelemetryCoordinator(
                new FallbackTokenProvider(77),
                new EmptyQuotaProvider(),
                new SqliteTelemetryRepository(Path.Combine(directory.FullName, "telemetry.db")),
                new SequencedObservatoryService(_ => Task.FromResult(Result(errors: 1))));

            var snapshot = await coordinator.RefreshAsync(RefreshTrigger.Manual, CancellationToken.None);

            Assert.True(snapshot.TokenDataFresh);
            Assert.Equal(77, Assert.Single(snapshot.TokenUsages).Breakdown.Total);
            Assert.NotNull(snapshot.TokenGeneration);
            Assert.True(snapshot.TokenGeneration!.IsFresh);
            Assert.True(snapshot.TokenGeneration.IsFallback);
            Assert.Equal(TelemetryDataQuality.Fallback, snapshot.TokenGeneration.Quality);
            Assert.Contains(snapshot.Sources, source =>
                source.Provider == "Tokscale fallback" && source.State == TelemetryHealthState.Live);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static CodexObservatoryRefreshResult Result(int errors, int filesDiscovered = 1) =>
        filesDiscovered == 0
            ? new(0, 0, 0, 0, 0, errors, 0)
            : new(1, 1, 1, 1, 1, errors, 100);

    private static CodexTokenAccountingSnapshot TokenSnapshot(long total)
    {
        var breakdown = new TokenBreakdown(total, 0, 0, 0, 0, total);
        return new CodexTokenAccountingSnapshot(
            "Native Codex",
            "local test coverage",
            [new TokenUsage("codex-native", "codex", "model", DateTimeOffset.UnixEpoch, breakdown)],
            [new TokenTimeBucket("codex-native", "1970-01-01 00:00", DateTimeOffset.UnixEpoch, breakdown)]);
    }

    private sealed class WritingObservatoryService(SqliteCodexObservatoryStore store) : ICodexObservatoryService
    {
        public bool Completed { get; private set; }

        public async Task<CodexObservatoryRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            await store.InitializeAsync(cancellationToken);
            await store.ApplyCumulativeTokenObservationAsync(
                new CodexCumulativeTokenObservation(
                    "committed-during-refresh",
                    "rollout.jsonl",
                    "session-a",
                    "session-a",
                    new DateTimeOffset(2026, 9, 1, 10, 5, 0, TimeSpan.Zero),
                    "model-a",
                    "high",
                    42,
                    0,
                    0,
                    0,
                    0,
                    42),
                cancellationToken);
            Completed = true;
            return Result(errors: 0);
        }
    }

    private sealed class SequencedObservatoryService(
        params Func<CancellationToken, Task<CodexObservatoryRefreshResult>>[] responses) : ICodexObservatoryService
    {
        private readonly Queue<Func<CancellationToken, Task<CodexObservatoryRefreshResult>>> _responses = new(responses);

        public Task<CodexObservatoryRefreshResult> RefreshAsync(CancellationToken cancellationToken) =>
            _responses.Dequeue()(cancellationToken);
    }

    private sealed class SequencedTokenProvider(params long[] totals) : ICodexTokenAccountingProvider
    {
        private readonly Queue<long> _totals = new(totals);
        public int Calls { get; private set; }

        public Task<CodexTokenAccountingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(TokenSnapshot(_totals.Dequeue()));
        }
    }

    private sealed class FallbackTokenProvider(long total) : ICodexTokenAccountingProvider
    {
        public Task<CodexTokenAccountingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(TokenSnapshot(total) with
            {
                Source = "Tokscale fallback",
                IsFallback = true,
                Diagnostic = "native unavailable"
            });
    }

    private sealed class EmptyQuotaProvider : ICodexQuotaProvider
    {
        public Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuotaSnapshot>>([]);
    }
}
