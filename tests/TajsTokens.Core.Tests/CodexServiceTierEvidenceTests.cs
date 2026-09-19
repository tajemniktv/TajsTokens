using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexServiceTierEvidenceTests
{
    private const string Session = "11111111-1111-4111-8111-111111111111";
    private const string Meta = """{"timestamp":"2026-09-18T10:00:00Z","type":"session_meta","payload":{"id":"11111111-1111-4111-8111-111111111111"}}""";
    private static string Settings(string value) => """{"timestamp":"2026-09-18T10:00:01Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":VALUE},"prompt":"never retain"}}""".Replace("VALUE", value);

    [Theory]
    [InlineData("\"priority\"", "priority")]
    [InlineData("\"default\"", "default")]
    [InlineData("\"FutureTier\"", "FutureTier")]
    [InlineData("null", null)]
    [InlineData("42", null)]
    [InlineData("\"\"", null)]
    public void TierIsSourceEvidenceNotNormalizedOrInferred(string value, string? expected)
    {
        var file = $"rollout-{Session}.jsonl";
        var state = new RolloutParseState(file, "source");
        var parser = new CodexRolloutParser();
        Assert.Null(parser.Parse(new(file, 0, 1, Settings(value)), state).WorkloadObservation);
        parser.Parse(new(file, 1, 2, Meta), state);
        var parsed = parser.Parse(new(file, 2, 3, Settings(value)), state);
        Assert.Equal(expected, parsed.WorkloadObservation!.ServiceTier);
        Assert.Equal("thread_settings_applied", parsed.WorkloadObservation.EventType);
        Assert.Null(parsed.TokenObservation);
        Assert.DoesNotContain("never retain", JsonSerializer.Serialize(parsed.WorkloadObservation));
        // Resuming does not invent a tier on a later turn or borrow one from an inherited prefix.
        var resumed = new RolloutParseState(file, "source", state.BuildResumeState(3));
        var turn = parser.Parse(new(file, 3, 4, """{"type":"turn_context","payload":{"model":"test"}}"""), resumed);
        Assert.Null(turn.WorkloadObservation!.ServiceTier);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TierMigrationReplayAppendAndReaderPreserveNullAndPhysicalSourceIdentity(bool desktop)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx"))) root = root.Parent;
        var directory = Path.Combine(root!.FullName, ".codex", "temp", "tier-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "telemetry.db");
            var path = Path.Combine(directory, desktop
                ? $"rollout-2026-09-18T10-00-00-{Session}_22222222-2222-4222-8222-222222222222.jsonl"
                : $"rollout-{Session}.jsonl");
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(default);
            using (var initial = new SqliteCodexObservatoryStore(database)) await initial.InitializeAsync(default);
            // Reconstruct the preceding component schema and verify migration does not invent tier values.
            await Execute(database, "ALTER TABLE codex_workload_observations DROP COLUMN service_tier; UPDATE observatory_schema SET version=6 WHERE component='codex-observatory';");
            using var store = new SqliteCodexObservatoryStore(database);
            var ingestion = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), repository, store);
            await File.WriteAllTextAsync(path, Meta + "\n" + Settings("\"priority\"") + "\n");
            await ingestion.IngestAsync(path, default);
            Assert.Equal(0, (await ingestion.IngestAsync(path, default)).RecordsScanned);
            // Force an old-parser checkpoint: the upgrade must replay rather than skip settings records.
            await Execute(database, desktop ? "UPDATE ingestion_checkpoints SET parser_version='typed-v6-service-tier-evidence';"
                : "UPDATE ingestion_checkpoints SET parser_version='typed-v5-quota-provenance';");
            Assert.True((await ingestion.IngestAsync(path, default)).RecordsScanned > 0);
            await File.AppendAllTextAsync(path, Settings("null") + "\n");
            var restarted = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), repository, store);
            await restarted.IngestAsync(path, default);
            var data = await new SqliteForecastDatasetReader(database).ReadAsync("codex", "default",
                DateTimeOffset.Parse("2026-09-18T00:00:00Z"), DateTimeOffset.Parse("2026-09-19T00:00:00Z"), default);
            var settings = data.Workload.Where(w => w.EventType == "thread_settings_applied").OrderBy(w => w.StartByteOffset).ToArray();
            Assert.Equal(2, settings.Length);
            Assert.All(settings, x => Assert.Equal(Session, x.SessionId));
            Assert.Equal("priority", settings[0].ServiceTier);
            Assert.Null(settings[1].ServiceTier);
            Assert.Equal(settings[0].SourceIdentity, settings[1].SourceIdentity);
            Assert.NotEqual(settings[0].SourceRecordId, settings[1].SourceRecordId);
            Assert.Empty(data.Tokens);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task Execute(string database, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + database);
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
