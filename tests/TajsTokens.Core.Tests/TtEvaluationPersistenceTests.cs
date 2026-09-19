using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class TtEvaluationPersistenceTests : IDisposable
{
    private readonly string _directory;
    private string Database => Path.Combine(_directory, "telemetry.db");

    public TtEvaluationPersistenceTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx"))) root = root.Parent;
        _directory = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Project root required."), ".codex", "temp", "tt-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task OriginalBasisAndResultsSurviveRetryConflictRestartAndLaterRestatement()
    {
        var repository = new SqliteTelemetryRepository(Database);
        var original = Snapshot();
        await repository.SaveTtEvaluationAsync(original, default);
        await repository.SaveTtEvaluationAsync(original, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveTtEvaluationAsync(original with
        { Report = original.Report with { Methodology = "changed" } }, default));
        var revised = original with { Id = Guid.NewGuid().ToString("N"), RecordedAtUtc = original.RecordedAtUtc.AddMinutes(1),
            Report = original.Report with { Methodology = "separate reconstruction" } };
        await repository.SaveTtEvaluationAsync(revised, default);
        var reopened = new SqliteTelemetryRepository(Database);
        var history = await reopened.GetTtEvaluationHistoryAsync("codex", "default", 10, default);
        Assert.Equal(2, history.Count);
        Assert.Equal(revised.Id, history[0].Id);
        Assert.All(history, x => Assert.Null(x.Problem));
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(history.Single(x => x.Id == original.Id).Snapshot));
        Assert.Empty(await reopened.GetTtEvaluationHistoryAsync("codex", "other-profile", 10, default));
        Assert.Single(await reopened.GetTtEvaluationHistoryAsync("codex", "default", 1, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SaveTtEvaluationAsync(original with
        { Id = Guid.NewGuid().ToString("N"), Report = original.Report with { Methodology = new string('x', 512 * 1024) } }, default));
    }

    [Fact]
    public async Task CorruptAndFutureRowsRemainVisibleWithoutReusingOrDeletingOriginals()
    {
        var repository = new SqliteTelemetryRepository(Database);
        var original = Snapshot();
        await repository.SaveTtEvaluationAsync(original, default);
        await Execute("UPDATE tt_evaluation_snapshots SET payload='{}'");
        var corrupt = Assert.Single(await repository.GetTtEvaluationHistoryAsync("codex", "default", 10, default));
        Assert.Null(corrupt.Snapshot);
        Assert.Equal("content-hash-mismatch", corrupt.Problem);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveTtEvaluationAsync(original, default));
        await Execute("UPDATE tt_evaluation_snapshots SET format_version=99");
        Assert.Equal("unsupported-format", Assert.Single(await repository.GetTtEvaluationHistoryAsync("codex", "default", 10, default)).Problem);
        await Execute("UPDATE tt_evaluation_snapshots SET recorded_at_utc='unknown'");
        Assert.Equal("invalid-save-time", Assert.Single(await repository.GetTtEvaluationHistoryAsync("codex", "default", 10, default)).Problem);
    }

    [Fact]
    public async Task BasisIdentityIsVerifiedInAdditionToPayloadChecksum()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.SaveTtEvaluationAsync(Snapshot(), default);
        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        var read = connection.CreateCommand(); read.CommandText = "SELECT payload FROM tt_evaluation_snapshots";
        var payload = ((string)(await read.ExecuteScalarAsync())!).Replace("tt:", "wrong:", StringComparison.Ordinal);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));
        var update = connection.CreateCommand();
        update.CommandText = "UPDATE tt_evaluation_snapshots SET payload=$payload,content_hash=$hash";
        update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", hash);
        await update.ExecuteNonQueryAsync();
        var entry = Assert.Single(await repository.GetTtEvaluationHistoryAsync("codex", "default", 10, default));
        Assert.Null(entry.Snapshot);
        Assert.Equal("unsupported-or-mismatched-basis", entry.Problem);
    }

    [Theory]
    [InlineData("cohort")]
    [InlineData("score")]
    [InlineData("scores")]
    [InlineData("report")]
    [InlineData("horizon")]
    [InlineData("range")]
    public async Task MalformedChecksummedSnapshotDoesNotHideValidNeighborsOrGetRewritten(string defect)
    {
        var repository = new SqliteTelemetryRepository(Database);
        var original = Snapshot();
        await repository.SaveTtEvaluationAsync(original, default);
        var bad = original with { Id = Guid.NewGuid().ToString("N"), RecordedAtUtc = original.RecordedAtUtc.AddMinutes(1) };
        bad = defect switch {
            "cohort" => bad with { Report = bad.Report with { Scores = [bad.Report.Scores[0] with { Cohort = null! }] } },
            "score" => bad with { Report = bad.Report with { Scores = [null!] } },
            "scores" => bad with { Report = bad.Report with { Scores = null! } },
            "report" => bad with { Report = null! },
            "horizon" => bad with { Report = bad.Report with { Scores = [bad.Report.Scores[0] with { HorizonHours = -1 }] } },
            _ => bad with { FromUtc = bad.ToUtc }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveTtEvaluationAsync(bad, default));
        var payload = JsonSerializer.Serialize(bad);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));
        await using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();
            var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO tt_evaluation_snapshots VALUES($id,$at,'codex','default',1,$hash,$payload)";
            insert.Parameters.AddWithValue("$id", bad.Id);
            insert.Parameters.AddWithValue("$at", bad.RecordedAtUtc.ToString("O"));
            insert.Parameters.AddWithValue("$hash", hash);
            insert.Parameters.AddWithValue("$payload", payload);
            await insert.ExecuteNonQueryAsync();
        }
        var history = await new SqliteTelemetryRepository(Database).GetTtEvaluationHistoryAsync("codex", "default", 10, default);
        Assert.Equal(2, history.Count);
        Assert.Equal(bad.Id, history[0].Id);
        Assert.Null(history[0].Snapshot);
        Assert.Equal("invalid-snapshot-metadata", history[0].Problem);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(history[1].Snapshot));
        Assert.Null(history[1].Problem);
        await using var check = new SqliteConnection($"Data Source={Database}");
        await check.OpenAsync();
        var read = check.CreateCommand();
        read.CommandText = "SELECT payload FROM tt_evaluation_snapshots WHERE snapshot_id=$id";
        read.Parameters.AddWithValue("$id", bad.Id);
        Assert.Equal(payload, await read.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AdditiveMigrationFailureLeavesVersionAndExistingEvidenceRecoverable()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeIntelligenceAsync(default);
        var quota = ComposedQuotaEvaluatorTests.TimelyData().Quota[0];
        await repository.UpsertQuotaSnapshotAsync(quota, default);
        await Execute("DROP TABLE tt_evaluation_snapshots; UPDATE intelligence_schema SET version=5; CREATE VIEW tt_evaluation_snapshots AS SELECT 1;");
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteTelemetryRepository(Database).InitializeIntelligenceAsync(default));
        Assert.Equal(5, await Scalar("SELECT version FROM intelligence_schema"));
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM quota_snapshots"));
        await Execute("DROP VIEW tt_evaluation_snapshots;");
        await new SqliteTelemetryRepository(Database).InitializeIntelligenceAsync(default);
        Assert.Equal(6, await Scalar("SELECT version FROM intelligence_schema"));
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM quota_snapshots"));
    }

    private static TtEvaluationSnapshot Snapshot()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        return new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, data.CapturedAtUtc,
            "codex", "default", data.CapturedAtUtc.AddDays(-1), data.CapturedAtUtc, TtEvaluator.Evaluate(data));
    }

    private async Task Execute(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private async Task<long> Scalar(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
}
