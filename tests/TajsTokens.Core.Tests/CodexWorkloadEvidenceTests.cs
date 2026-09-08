using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class CodexWorkloadEvidenceTests
{
    private const string Session = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void NativeEffortAndTurnIdentityAreRetainedWithoutContent()
    {
        var parser = new CodexRolloutParser();
        var state = new RolloutParseState($"rollout-{Session}.jsonl", "identity");
        ParsedRolloutRecord Parse(string type, object payload, int offset) => parser.Parse(new RawSessionRecord(
            state.FilePath, offset, offset + 1, JsonSerializer.Serialize(new { type, timestamp = "2026-09-01T00:00:00Z", payload })), state);
        Parse("session_meta", new { id = Session }, 0);
        var context = Parse("turn_context", new { turn_id = "turn-a", root_turn_id = "root-a", model = "model-a", effort = "max", developer_instructions = "private-content-do-not-retain" }, 1);
        Assert.Equal("max", state.ReasoningEffort);
        Assert.Equal("turn-a", context.WorkloadObservation!.TurnId);
        Assert.Equal("root-a", context.WorkloadObservation.RootTurnId);
        Assert.DoesNotContain("private-content", JsonSerializer.Serialize(context.WorkloadObservation));
        Parse("turn_context", new { turn_id = "turn-b", model = "model-b" }, 2);
        Assert.Null(state.ReasoningEffort);
        var complete = Parse("event_msg", new { type = "task_complete", turn_id = "turn-b", last_agent_message = "private-content-do-not-retain" }, 3);
        Assert.Equal("task_complete", complete.WorkloadObservation!.EventType);
        Assert.Equal("turn-b", complete.WorkloadObservation.TurnId);
        Assert.DoesNotContain("private-content", JsonSerializer.Serialize(complete.WorkloadObservation));
    }

    [Fact]
    public void MissingSourceTimestampRemainsUnknownAndInheritedContextIsIgnored()
    {
        var parser = new CodexRolloutParser();
        var state = new RolloutParseState($"rollout-{Session}.jsonl", "identity");
        ParsedRolloutRecord Parse(string json) => parser.Parse(new RawSessionRecord(state.FilePath, 0, 1, json), state);
        var inherited = Parse("""{"type":"turn_context","payload":{"turn_id":"parent-turn","effort":"high"}}""");
        Assert.Null(inherited.WorkloadObservation);
        Parse(JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = Session } }));
        var own = Parse("""{"type":"turn_context","payload":{"turn_id":"own-turn","effort":null,"reasoning_effort":"high"}}""");
        Assert.Null(own.WorkloadObservation!.ObservedAtUtc);
        Assert.Null(own.WorkloadObservation.ReasoningEffort);
        Assert.Null(state.ReasoningEffort);
    }

    [Fact]
    public async Task ReplayRepairsMissingEffortWithoutRecountingTokensAndMetadataIsIdempotent()
    {
        var directory = Directory.CreateTempSubdirectory("tajs-workload-");
        try
        {
            var path = Path.Combine(directory.FullName, "test.db");
            var repository = new SqliteTelemetryRepository(path);
            await repository.InitializeAsync(CancellationToken.None);
            using var store = new SqliteCodexObservatoryStore(path);
            var time = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
            CodexTokenCountObservation Token(string? effort) => new("event", "source.jsonl", Session, Session, time, "model", effort,
                new CodexTokenUsageSnapshot(10, 0, 0, 5, 0, 15), null);
            await store.ApplyCumulativeTokenObservationAsync(Token(null), CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(Token("high"), CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(Token("low"), CancellationToken.None);
            var metadata = new CodexWorkloadObservation("context", "identity", "source.jsonl", 0, 123, Session,
                "turn_context", time, time.AddDays(1), "turn", null, null, "model", "high", null);
            await store.UpsertWorkloadObservationAsync(metadata, CancellationToken.None);
            await store.UpsertWorkloadObservationAsync(metadata with { CapturedAtUtc = time.AddDays(2) }, CancellationToken.None);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) || ':' || sum(reported_total_tokens) || ':' || min(reasoning_effort) FROM codex_native_token_events";
            Assert.Equal("1:15:high", command.ExecuteScalar());
            command.CommandText = "SELECT count(*) || ':' || min(captured_at_utc) FROM codex_workload_observations";
            Assert.Equal("1:2026-09-02T00:00:00.0000000+00:00", command.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }
}
