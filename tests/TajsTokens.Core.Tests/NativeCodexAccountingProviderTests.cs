using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;

namespace TajsTokens.Core.Tests;

public sealed class NativeCodexAccountingProviderTests
{
    [Fact]
    public async Task SqliteProjection_ProducesDisjointModelAndHourlyGenerations()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-accounting-");
        var databasePath = Path.Combine(directory.FullName, "telemetry.db");
        var start = new DateTimeOffset(2026, 9, 1, 10, 5, 0, TimeSpan.Zero);

        try
        {
            var repository = new SqliteTelemetryRepository(databasePath);
            var store = new SqliteCodexObservatoryStore(databasePath);
            await repository.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);

            await store.ApplyCumulativeTokenObservationAsync(
                Token("a1", "session-a", start, "model-a", 100, 80, 0, 10, 4, 110),
                CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(
                Token("a2", "session-a", start.AddHours(1), "model-a", 150, 100, 0, 20, 5, 170),
                CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(
                Token("b1", "session-b", start.AddHours(1).AddMinutes(10), "model-b", 120, 20, 0, 30, 5, 150),
                CancellationToken.None);

            var snapshot = await new SqliteNativeCodexAccountingProvider(databasePath)
                .GetSnapshotAsync(CancellationToken.None);

            Assert.Equal("Native Codex", snapshot.Source);
            Assert.Contains("Local normalized", snapshot.Coverage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, snapshot.Usage.Count);

            var modelA = snapshot.Usage.Single(item => item.Model == "model-a").Breakdown;
            Assert.Equal(50, modelA.UncachedInput);
            Assert.Equal(100, modelA.CacheRead);
            Assert.Equal(0, modelA.CacheWrite);
            Assert.Equal(15, modelA.NonReasoningOutput);
            Assert.Equal(5, modelA.ReasoningOutput);
            Assert.Equal(170, modelA.Total);

            var modelB = snapshot.Usage.Single(item => item.Model == "model-b").Breakdown;
            Assert.Equal(100, modelB.UncachedInput);
            Assert.Equal(20, modelB.CacheRead);
            Assert.Equal(25, modelB.NonReasoningOutput);
            Assert.Equal(5, modelB.ReasoningOutput);
            Assert.Equal(150, modelB.Total);

            Assert.Equal(2, snapshot.Hourly.Count);
            var firstHour = snapshot.Hourly.Single(item => item.StartUtc == start.AddMinutes(-5));
            Assert.Equal(110, firstHour.Breakdown.Total);
            var secondHour = snapshot.Hourly.Single(item => item.StartUtc == start.AddMinutes(-5).AddHours(1));
            Assert.Equal(210, secondHour.Breakdown.Total);
            Assert.All(snapshot.Hourly, item => Assert.Equal("codex-native", item.Provider));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task NativeFirst_DefaultPath_DoesNotInvokeTokscale()
    {
        var nativeSnapshot = Snapshot("Native Codex", "codex-native", 123);
        var native = new StaticProvider(nativeSnapshot);
        var tokscale = new CountingTokscaleProvider(Snapshot("Tokscale", "tokscale", 123));
        var provider = new NativeFirstCodexAccountingProvider(
            native,
            tokscale,
            reconciliationEnabled: () => false,
            fallbackEnabled: () => false);

        var actual = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Same(nativeSnapshot, actual);
        Assert.Equal(1, native.Calls);
        Assert.Equal(0, tokscale.UsageCalls);
        Assert.Equal(0, tokscale.HourlyCalls);
    }

    [Fact]
    public async Task NativeFirst_ReconciliationReturnsNativeAndReportsReferenceDelta()
    {
        var native = new StaticProvider(Snapshot("Native Codex", "codex-native", 120));
        var tokscale = new CountingTokscaleProvider(Snapshot("Tokscale", "tokscale", 100));
        var provider = new NativeFirstCodexAccountingProvider(
            native,
            tokscale,
            reconciliationEnabled: () => true,
            fallbackEnabled: () => false);

        var actual = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal("Native Codex", actual.Source);
        Assert.NotNull(actual.Reconciliation);
        Assert.Equal(20, actual.Reconciliation!.TotalDifference);
        Assert.Equal(20d, actual.Reconciliation.TotalDifferencePercent, precision: 6);
        Assert.False(actual.Reconciliation.Exact);
        Assert.Contains("total Δ", actual.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(1, tokscale.UsageCalls);
        Assert.Equal(1, tokscale.HourlyCalls);
    }

    [Fact]
    public void Reconciliation_UsesCommonLabelsWhenReferenceHasNoUtcTimestamp()
    {
        var breakdown = new TokenBreakdown(100, 20, 0, 5, 1, 126);
        var native = new CodexTokenAccountingSnapshot(
            "Native Codex",
            "test",
            [new TokenUsage("codex-native", "codex", "model", DateTimeOffset.UnixEpoch, breakdown)],
            [new TokenTimeBucket("codex-native", "2026-09-01 13:00", new DateTimeOffset(2026, 9, 1, 11, 0, 0, TimeSpan.Zero), breakdown)]);
        var reference = new CodexTokenAccountingSnapshot(
            "Tokscale",
            "test",
            [new TokenUsage("tokscale", "codex", "model", DateTimeOffset.UnixEpoch, breakdown)],
            [new TokenTimeBucket("tokscale", "2026-09-01 13:00", null, breakdown)]);

        var reconciliation = NativeFirstCodexAccountingProvider.Reconcile(native, reference);

        Assert.True(reconciliation.Exact);
        Assert.Equal(1, reconciliation.HourBucketsCompared);
        Assert.Equal(0, reconciliation.HourBucketsDifferent);
    }

    [Fact]
    public async Task NativeFirst_FallbackIsExplicitAndMarkedAsFallback()
    {
        var native = new ThrowingProvider(new InvalidOperationException("native unavailable"));
        var tokscale = new CountingTokscaleProvider(Snapshot("Tokscale", "tokscale", 100));
        var provider = new NativeFirstCodexAccountingProvider(
            native,
            tokscale,
            reconciliationEnabled: () => false,
            fallbackEnabled: () => true);

        var actual = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.True(actual.IsFallback);
        Assert.Equal("Tokscale fallback", actual.Source);
        Assert.Contains("native unavailable", actual.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, tokscale.UsageCalls);
        Assert.Equal(1, tokscale.HourlyCalls);
    }

    private static CodexCumulativeTokenObservation Token(
        string eventId,
        string sessionId,
        DateTimeOffset observed,
        string model,
        long input,
        long cached,
        long cacheWrite,
        long output,
        long reasoning,
        long total) => new(
        eventId,
        $"{sessionId}.jsonl",
        sessionId,
        sessionId,
        observed,
        model,
        "high",
        input,
        cached,
        cacheWrite,
        output,
        reasoning,
        total);

    private static CodexTokenAccountingSnapshot Snapshot(string source, string provider, long tokens)
    {
        var breakdown = new TokenBreakdown(tokens, 0, 0, 0, 0, tokens);
        return new CodexTokenAccountingSnapshot(
            source,
            "test coverage",
            [new TokenUsage(provider, "codex", "model", DateTimeOffset.UnixEpoch, breakdown)],
            [new TokenTimeBucket(provider, "1970-01-01 00:00", DateTimeOffset.UnixEpoch, breakdown)]);
    }

    private sealed class StaticProvider(CodexTokenAccountingSnapshot snapshot) : ICodexTokenAccountingProvider
    {
        public int Calls { get; private set; }

        public Task<CodexTokenAccountingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class ThrowingProvider(Exception exception) : ICodexTokenAccountingProvider
    {
        public Task<CodexTokenAccountingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromException<CodexTokenAccountingSnapshot>(exception);
    }

    private sealed class CountingTokscaleProvider(CodexTokenAccountingSnapshot snapshot) : ITokscaleProvider
    {
        public int UsageCalls { get; private set; }
        public int HourlyCalls { get; private set; }

        public Task<IReadOnlyList<TokenUsage>> GetUsageObservationsAsync(CancellationToken cancellationToken)
        {
            UsageCalls++;
            return Task.FromResult(snapshot.Usage);
        }

        public Task<IReadOnlyList<TokenTimeBucket>> GetHourlyUsageAsync(CancellationToken cancellationToken)
        {
            HourlyCalls++;
            return Task.FromResult(snapshot.Hourly);
        }
    }
}
