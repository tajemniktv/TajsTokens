using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class ServerEvidenceScopeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TajsTokens.ServerScope", Guid.NewGuid().ToString("N"));
    private readonly string _database;
    private readonly SqliteTelemetryRepository _repository;
    private readonly SqliteCodexObservatoryStore _store;
    private readonly RecordingProvider _provider = new();
    private readonly CodexServerEvidenceService _service;

    public ServerEvidenceScopeTests()
    {
        Directory.CreateDirectory(_directory);
        _database = Path.Combine(_directory, "telemetry.db");
        _repository = new(_database);
        _store = new(_database);
        _service = new(_database, _repository, _provider);
    }

    [Fact]
    public async Task SelectionReportsItsExaminedDenominatorAndTruncationWithoutDroppingUnknownMetadataAmounts()
    {
        await _repository.InitializeAsync(default);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var observations = Enumerable.Range(0, 108).Select(i => new CodexTokenCountObservation(
            "event-" + i, "file-" + i, i < 106 ? Guid.NewGuid().ToString() : "invalid-" + i,
            null, now.AddHours(-3).AddMinutes(i), null, null, 1, 0, 0, 0, 0, 1)
        { CapturedAtUtc = now }).ToArray();
        await _store.ApplyCumulativeTokenObservationsAsync(observations, default);
        CodexServerCollectionScope scope = await _service.SelectThreadSelectionAsync(default);
        Assert.Equal(100, scope.CandidateRowsExamined);
        Assert.Equal(98, scope.EligibleThreadsInExaminedRows);
        Assert.True(scope.CandidateScanTruncated);
        Assert.Equal(6, scope.RequestedThreadIds.Count);
        Assert.True(scope.HasSupportedShape());
        Assert.Contains("truncated", scope.ToDisplayText());
        Assert.Contains("30 minutes", scope.ToDisplayText());
        Assert.Equal(108L, await Scalar("SELECT SUM(reported_total_tokens) FROM codex_native_token_events"));
        Assert.Equal(108L, await Scalar("SELECT COUNT(*) FROM codex_native_token_events WHERE model IS NULL AND reasoning_effort IS NULL"));
    }

    [Fact]
    public async Task RotationPrioritizesUnrequestedThreadOverMoreRecentActivity()
    {
        string older = Guid.NewGuid().ToString();
        string newer = Guid.NewGuid().ToString();
        await Seed(older, newer);
        var first = await _service.SelectThreadSelectionAsync(default);
        Assert.Equal(newer, first.RequestedThreadIds[0]);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _repository.SaveServerEvidenceAsync(new([
            CodexServerEvidenceParser.ParseUsage("{\"result\":{\"threadUsage\":null}}", newer, now, now)]), default);
        var rotated = await _service.SelectThreadSelectionAsync(default);
        Assert.Equal(older, rotated.RequestedThreadIds[0]);
        Assert.Equal(2, rotated.CandidateRowsExamined);
        Assert.False(rotated.CandidateScanTruncated);
    }

    [Fact]
    public async Task BatchScopeSurvivesStorageAndCadenceDoesNotCreateExtraAttempts()
    {
        string older = Guid.NewGuid().ToString();
        string newer = Guid.NewGuid().ToString();
        await Seed(older, newer);
        await _service.CollectAsync(false, default);
        await _service.CollectAsync(false, default);
        Assert.Equal(1, _provider.Calls);
        await _service.CollectAsync(true, default);
        Assert.Equal(2, _provider.Calls);
        var history = await _service.ReadHistoryAsync(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMinutes(1), null, default);
        Assert.Equal(6, history.Observations.Count);
        Assert.Equal(2, history.Observations.Select(x => x.CollectionScope!.CollectionId).Distinct().Count());
        Assert.All(history.Observations, row =>
        {
            Assert.True(row.CollectionScope!.HasSupportedShape());
            Assert.Equal(2, row.CollectionScope.RequestedThreadIds.Count);
            Assert.Contains(older, row.CollectionScope.RequestedThreadIds);
            Assert.Contains(newer, row.CollectionScope.RequestedThreadIds);
        });
        Assert.Contains("2/6 requested", CodexServerCollectionScope.DescribeLatest(history.Observations));
        Assert.Equal(2L, await Scalar("SELECT SUM(reported_total_tokens) FROM codex_native_token_events"));
    }

    [Fact]
    public async Task LegacyCaptureDoesNotAcquireInventedScopeAndScopeChangesCannotOverwriteAnObservation()
    {
        await Seed(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CodexServerObservation legacy = CodexServerEvidenceParser.ParseUsage("{\"result\":{\"summary\":{\"lifetimeTokens\":0}}}", null, now, now);
        Assert.DoesNotContain("CollectionScope", JsonSerializer.Serialize(legacy));
        await _repository.SaveServerEvidenceAsync(new([legacy]), default);
        var history = await _service.ReadHistoryAsync(now.AddMinutes(-1), now.AddMinutes(1), null, default);
        Assert.Null(Assert.Single(history.Observations).CollectionScope);
        Assert.Contains("not recorded", CodexServerCollectionScope.DescribeLatest(history.Observations));
        CodexServerCollectionScope scope = await _service.SelectThreadSelectionAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.SaveServerEvidenceAsync(new([legacy with { CollectionScope = scope }]), default));
        Assert.Null(Assert.Single((await _service.ReadHistoryAsync(now.AddMinutes(-1), now.AddMinutes(1), null, default)).Observations).CollectionScope);
    }

    [Fact]
    public async Task InvalidScopeIsRejectedAndInvalidRetainedShapeHasAnUnknownDisplay()
    {
        await Seed(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        CodexServerCollectionScope valid = await _service.SelectThreadSelectionAsync(default);
        CodexServerCollectionScope invalid = valid with { RequestedThreadIds = null! };
        Assert.False(invalid.HasSupportedShape());
        Assert.Contains("unknown", invalid.ToDisplayText());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CodexServerObservation observation = CodexServerEvidenceParser.ParseUsage("{\"result\":{\"summary\":{\"lifetimeTokens\":0}}}", null, now, now)
            with { CollectionScope = invalid };
        await Assert.ThrowsAsync<ArgumentException>(() => _repository.SaveServerEvidenceAsync(new([observation]), default));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_server_evidence"));
    }

    [Theory]
    [InlineData("longestRunningTurnSec")]
    [InlineData("currentStreakDays")]
    [InlineData("longestStreakDays")]
    public void NonTokenActivityFieldsAreReportedEvidenceRatherThanAnEmptyReport(string field)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CodexServerObservation row = CodexServerEvidenceParser.ParseUsage("{\"result\":{\"summary\":{\"" + field + "\":1}}}", null, now, now);
        Assert.Equal(ServerEvidenceState.Available, row.State);
        Assert.Null(row.Activity!.LifetimeTokens);
    }

    [Fact]
    public void AllNullActivityIsUnknownAndExplicitEmptyDaysDoNotClaimAnAccountWideZero()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var unknown = CodexServerEvidenceParser.ParseUsage("{\"result\":{\"summary\":{},\"dailyUsageBuckets\":null}}", null, now, now);
        var empty = CodexServerEvidenceParser.ParseUsage("{\"result\":{\"summary\":{},\"dailyUsageBuckets\":[]}}", null, now, now);
        Assert.Equal(ServerEvidenceState.Unavailable, unknown.State);
        Assert.Equal(ServerEvidenceState.Empty, empty.State);
        Assert.Null(empty.Activity!.LifetimeTokens);
        Assert.Contains("not asserted", empty.Detail);
    }

    private async Task Seed(string older, string newer)
    {
        await _repository.InitializeAsync(default);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _store.ApplyCumulativeTokenObservationsAsync([
            new("older", "older.jsonl", older, null, now.AddHours(-2), null, null, 1,0,0,0,0,1) { CapturedAtUtc = now },
            new("newer", "newer.jsonl", newer, null, now.AddHours(-1), null, null, 1,0,0,0,0,1) { CapturedAtUtc = now }], default);
    }

    private async Task<object?> Scalar(string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + _database);
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private sealed class RecordingProvider : ICodexServerEvidenceProvider
    {
        public int Calls { get; private set; }
        public Task<CodexServerCollection> CollectAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
        {
            Calls++;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CodexServerCollection(new[]
            {
                CodexServerEvidenceParser.ParseUsage("{\"result\":{\"summary\":{\"lifetimeTokens\":0}}}", null, now, now),
            }.Concat(ids.Select(id => CodexServerEvidenceParser.ParseUsage("{\"result\":{\"threadUsage\":null}}", id, now, now))).ToArray()));
        }
    }

    public void Dispose()
    {
        _store.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true);
    }
}
