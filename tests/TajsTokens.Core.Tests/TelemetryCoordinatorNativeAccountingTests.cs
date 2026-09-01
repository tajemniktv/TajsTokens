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

    private sealed class MarkingObservatoryService : ICodexObservatoryService
    {
        public bool Completed { get; private set; }

        public Task<CodexObservatoryRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            Completed = true;
            return Task.FromResult(new CodexObservatoryRefreshResult(1, 1, 1, 1, 1, 0, 100));
        }
    }

    private sealed class OrderCheckingTokenProvider(Func<bool> observatoryCompleted) : ICodexTokenAccountingProvider
    {
        public bool ObservedCompletedObservatory { get; private set; }

        public Task<CodexTokenAccountingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            ObservedCompletedObservatory = observatoryCompleted();
            var breakdown = new TokenBreakdown(42, 0, 0, 0, 0, 42);
            return Task.FromResult(new CodexTokenAccountingSnapshot(
                "Native Codex",
                "local test coverage",
                [new TokenUsage("codex-native", "codex", "model", DateTimeOffset.UnixEpoch, breakdown)],
                [new TokenTimeBucket("codex-native", "1970-01-01 00:00", DateTimeOffset.UnixEpoch, breakdown)]));
        }
    }

    private sealed class EmptyQuotaProvider : ICodexQuotaProvider
    {
        public Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuotaSnapshot>>([]);
    }
}
