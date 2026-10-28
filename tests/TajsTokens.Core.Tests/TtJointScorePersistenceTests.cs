using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class TtJointScorePersistenceTests : IDisposable
{
    private readonly string _directory;
    public TtJointScorePersistenceTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx"))) root = root.Parent;
        _directory = Path.Combine(root!.FullName, ".codex", "temp", "tt-joint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }
    private string Database => Path.Combine(_directory, "telemetry.db");
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
    private static TtJointScoreSnapshot Snapshot(string id = "original")
    {
        var at = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        var basis = TtJointWorkloadBasisTests.Basis(); var work = TtJointWorkloadBasisTests.Workload();
        return new(id, at, at, "codex", "fixture-profile", at.AddHours(-1), at, basis, work, basis.ScoreExact(work));
    }
    private async Task Execute(string sql, string? payload = null)
    {
        using var connection = new SqliteConnection("Data Source=" + Database); await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        if (payload is not null)
        {
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$hash", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
        }
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task OptionalAssessmentPreservesOldPayloadsAndFreezesNewPartialResultsAcrossRestart()
    {
        var repository = new SqliteTelemetryRepository(Database); var original = Snapshot();
        Assert.DoesNotContain("Assessment", JsonSerializer.Serialize(original));
        await repository.SaveTtJointScoreAsync(original, default);
        var unsupported = new CodexJointWorkload(CodexJointWorkload.Version, original.Workload.Kind,
            original.Workload.Cells.Select(x => x with { Coordinate = x.Coordinate! with { Model = "unmapped" } }).ToArray(), null, 0, 0);
        var partial = original with { Id = "partial", Workload = unsupported, ExactTt = null,
            Assessment = TtJointScoring.Evaluate(original.Basis, unsupported) };
        await repository.SaveTtJointScoreAsync(partial, default);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveTtJointScoreAsync(partial with { Assessment = partial.Assessment! with { CoveredSubtotalTt = 999 } }, default));
        var history = await new SqliteTelemetryRepository(Database).GetTtJointScoreHistoryAsync(original.Provider, original.Profile, 20, default);
        Assert.Null(history.Single(x => x.Id == original.Id).Snapshot!.Assessment);
        Assert.Equal(partial.Assessment, history.Single(x => x.Id == partial.Id).Snapshot!.Assessment);
        Assert.Equal(TtScoreState.Partial, partial.Assessment!.State);
        var tampered = JsonNode.Parse(JsonSerializer.Serialize(partial))!;
        tampered["Assessment"]!["CoveredSubtotalTt"] = 999;
        await Execute("UPDATE tt_joint_score_snapshots SET payload=$payload,content_hash=$hash WHERE snapshot_id='partial'", tampered.ToJsonString());
        var checkedHistory = await repository.GetTtJointScoreHistoryAsync(original.Provider, original.Profile, 20, default);
        Assert.NotNull(checkedHistory.Single(x => x.Id == "partial").Problem);
        Assert.NotNull(checkedHistory.Single(x => x.Id == original.Id).Snapshot);
    }
    private async Task<long> Scalar(string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + Database); await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql; return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task OriginalAndExplicitRestatementRetainTheirOwnBasisAndScoreAcrossRetryConflictAndRestart()
    {
        var repository = new SqliteTelemetryRepository(Database); var original = Snapshot();
        await repository.SaveTtJointScoreAsync(original, default); await repository.SaveTtJointScoreAsync(original, default);
        var nextBasis = TtJointWorkloadBasisTests.Basis(4);
        var restated = original with { Id = "restated", RecordedAtUtc = original.RecordedAtUtc.AddMinutes(1),
            Basis = nextBasis, ExactTt = nextBasis.ScoreExact(original.Workload), RestatesSnapshotId = original.Id };
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveTtJointScoreAsync(restated with { Id = original.Id, RestatesSnapshotId = null }, default));
        await repository.SaveTtJointScoreAsync(restated, default);
        var reopened = new SqliteTelemetryRepository(Database);
        var history = await reopened.GetTtJointScoreHistoryAsync("codex", "fixture-profile", 20, default);
        Assert.Equal(2, history.Count); Assert.All(history, x => Assert.Null(x.Problem));
        var savedOriginal = history.Single(x => x.Id == original.Id).Snapshot!;
        var savedRestatement = history.Single(x => x.Id == restated.Id).Snapshot!;
        Assert.Equal(original.Basis.BasisId, savedOriginal.Basis.BasisId); Assert.Equal(1.5m, savedOriginal.ExactTt);
        Assert.Equal(nextBasis.BasisId, savedRestatement.Basis.BasisId); Assert.NotEqual(savedOriginal.ExactTt, savedRestatement.ExactTt);
        Assert.Equal(original.Id, savedRestatement.RestatesSnapshotId); Assert.Equal(original.Workload, savedRestatement.Workload);
        Assert.Single(await reopened.GetTtJointScoreHistoryAsync("codex", "fixture-profile", 1, default));
        Assert.Empty(await reopened.GetTtJointScoreHistoryAsync("codex", "another-profile", 20, default));
        Assert.Empty(await reopened.GetTtEvaluationHistoryAsync("codex", "fixture-profile", 20, default));
    }

    [Fact]
    public async Task RestatementRequiresAnIntactEarlierScoreOfExactlyTheSameScopeAndCapturedWorkload()
    {
        var repository = new SqliteTelemetryRepository(Database); var original = Snapshot();
        await repository.SaveTtJointScoreAsync(original, default);
        var restated = original with { Id = "restated", RestatesSnapshotId = original.Id, RecordedAtUtc = original.RecordedAtUtc.AddMinutes(1) };
        var different = TtJointWorkloadBasisTests.Workload(300);
        foreach (var bad in new[] { restated with { RestatesSnapshotId = "missing" }, restated with { Profile = "another-profile" },
            restated with { FromUtc = original.FromUtc.AddMinutes(1) }, restated with { DatasetCapturedAtUtc = original.DatasetCapturedAtUtc.AddMinutes(1) },
            restated with { RecordedAtUtc = original.RecordedAtUtc.AddMinutes(-1), DatasetCapturedAtUtc = original.DatasetCapturedAtUtc.AddMinutes(-2) },
            restated with { Workload = different, ExactTt = original.Basis.ScoreExact(different) } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveTtJointScoreAsync(bad, default));
        Assert.Single(await repository.GetTtJointScoreHistoryAsync("codex", "fixture-profile", 20, default));
        await Execute("UPDATE tt_joint_score_snapshots SET content_hash='tampered' WHERE snapshot_id='original'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveTtJointScoreAsync(restated, default));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("basis-id")]
    [InlineData("normalizer")]
    [InlineData("normalizer-overflow")]
    [InlineData("score")]
    [InlineData("contract")]
    [InlineData("future-format")]
    [InlineData("oversized")]
    public async Task BadRowsAreUnavailableWithoutHidingIntactNeighborsEvenWithARefreshedChecksum(string defect)
    {
        var repository = new SqliteTelemetryRepository(Database); var original = Snapshot();
        await repository.SaveTtJointScoreAsync(original, default);
        await repository.SaveTtJointScoreAsync(original with { Id = "good-neighbor" }, default);
        var node = JsonNode.Parse(JsonSerializer.Serialize(original))!;
        switch (defect)
        {
            case "checksum": await Execute("UPDATE tt_joint_score_snapshots SET content_hash='bad' WHERE snapshot_id='original'"); break;
            case "future-format": await Execute("UPDATE tt_joint_score_snapshots SET format_version=2 WHERE snapshot_id='original'"); break;
            case "oversized": await Execute("UPDATE tt_joint_score_snapshots SET payload=printf('%.*c',524289,'x') WHERE snapshot_id='original'"); break;
            default:
                if (defect == "basis-id") node["Basis"]!["BasisId"] = "tt-joint:" + new string('f', 64);
                if (defect == "normalizer") node["Basis"]!["ReferenceScore"] = 999;
                if (defect == "normalizer-overflow") node["Basis"]!["ReferenceScore"] = JsonNode.Parse("1e1000");
                if (defect == "score") node["ExactTt"] = 999;
                if (defect == "contract") node["Contract"] = "future-contract";
                await Execute("UPDATE tt_joint_score_snapshots SET payload=$payload,content_hash=$hash WHERE snapshot_id='original'", node.ToJsonString());
                break;
        }
        var history = await new SqliteTelemetryRepository(Database).GetTtJointScoreHistoryAsync("codex", "fixture-profile", 20, default);
        Assert.Equal(2, history.Count); Assert.NotNull(history.Single(x => x.Id == "original").Problem);
        Assert.Null(history.Single(x => x.Id == "original").Snapshot);
        Assert.NotNull(history.Single(x => x.Id == "good-neighbor").Snapshot);
    }

    [Fact]
    public async Task JointArchiveMigrationIsAtomicAndDoesNotRewriteTheLegacyResearchArchive()
    {
        var repository = new SqliteTelemetryRepository(Database); await repository.InitializeIntelligenceAsync(default);
        await Execute("""
            INSERT INTO tt_evaluation_snapshots VALUES('legacy','2026-10-01T00:00:00Z','codex','fixture',1,'original-hash','original-bytes');
            DROP TABLE tt_joint_score_snapshots; UPDATE intelligence_schema SET version=7;
            CREATE VIEW tt_joint_score_snapshots AS SELECT 1;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteTelemetryRepository(Database).InitializeIntelligenceAsync(default));
        Assert.Equal(7, await Scalar("SELECT version FROM intelligence_schema"));
        await Execute("DROP VIEW tt_joint_score_snapshots;");
        await new SqliteTelemetryRepository(Database).InitializeIntelligenceAsync(default);
        Assert.Equal(9, await Scalar("SELECT version FROM intelligence_schema"));
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM tt_evaluation_snapshots WHERE snapshot_id='legacy' AND content_hash='original-hash' AND payload='original-bytes'"));
    }

    [Fact]
    public async Task InvalidScoresAndConditionalWorkloadCannotBeArchivedAsObservedResults()
    {
        var repository = new SqliteTelemetryRepository(Database); var original = Snapshot();
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveTtJointScoreAsync(original with { ExactTt = 999 }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveTtJointScoreAsync(original with { Workload = new(CodexJointWorkload.Version,
            CodexJointQuantityKind.ConditionalProjection, original.Workload.Cells, null, 0, 0) }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveTtJointScoreAsync(original with { RestatesSnapshotId = original.Id }, default));
    }
}
