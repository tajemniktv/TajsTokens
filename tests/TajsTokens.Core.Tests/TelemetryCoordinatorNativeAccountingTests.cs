using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class TelemetryCoordinatorNativeAccountingTests
{
    [Fact]
    public async Task Refresh_ProjectsTokenGenerationAfterObservatoryCompletes()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-refresh-order-");
        try
        {
            var observatory = new MarkingObservatoryService();
            var tokens = new OrderCheckingTokenProvider(() => observatory.Completed);
            var coordinator = new TelemetryCoordinator(
                tokens,
                new EmptyQuotaProvider(),
                new SqliteTelemetryRepository(Path.Combine(directory.FullName, "telemetry.db")),
                observatory);

            var snapshot = await coordinator.RefreshAsync(RefreshTrigger.Manual, CancellationToken.None);

            Assert.True(observatory.Completed);
            Assert.True(tokens.ObservedCompletedObservatory);
            Assert.True(snapshot.TokenDataFresh);
            Assert.Equal(42, Assert.Single(snapshot.TokenUsages).Breakdown.Total);
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

    private static CodexObservatoryRefreshResult Result(int errors) =>
        new(1, 1, 1, 1, 1, errors, 100);

    private static CodexTokenAccountingSnapshot TokenSnapshot(long total)
    {
        var breakdown = new TokenBreakdown(total, 0, 0, 0, 0, total);
        return new CodexTokenAccountingSnapshot(
            "Native Codex",
            "local test coverage",
            [new TokenUsage("codex-native", "codex", "model", DateTimeOffset.UnixEpoch, breakdown)],
            [new TokenTimeBucket("codex-native", "1970-01-01 00:00", DateTimeOffset.UnixEpoch, breakdown)]);
    }

    private sealed class MarkingObservatoryService : ICodexObservatoryService
    {
        public bool Completed { get; private set; }

        public Task<CodexObservatoryRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            Completed = true;
            return Task.FromResult(Result(errors: 0));
        }
    }

    private sealed class OrderCheckingTokenProvider(Func<bool> observatoryCompleted) : ICodexTokenAccountingProvider
    {
        public bool ObservedCompletedObservatory { get; private set; }

        public Task<CodexTokenAccountingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            ObservedCompletedObservatory = observatoryCompleted();
            return Task.FromResult(TokenSnapshot(42));
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

    private sealed class EmptyQuotaProvider : ICodexQuotaProvider
    {
        public Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuotaSnapshot>>([]);
    }
}
