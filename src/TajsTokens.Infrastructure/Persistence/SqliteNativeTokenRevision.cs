using Microsoft.Data.Sqlite;

namespace TajsTokens.Infrastructure.Persistence;

/// <summary>Derived projection invalidation, separate from broad evidence/forecast freshness.
/// Triggers cover both writers, repair, retirement and app-owned maintenance atomically.</summary>
internal static class SqliteNativeTokenRevision
{
    public static async Task EnsureAsync(SqliteConnection connection, CancellationToken token)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS codex_native_accounting_revision (
                id INTEGER PRIMARY KEY CHECK(id = 1), revision INTEGER NOT NULL);
            INSERT INTO codex_native_accounting_revision(id, revision) VALUES(1, 0) ON CONFLICT(id) DO NOTHING;
            CREATE TABLE IF NOT EXISTS codex_native_token_projection_revision (
                id INTEGER PRIMARY KEY CHECK(id = 1), revision INTEGER NOT NULL);
            INSERT INTO codex_native_token_projection_revision(id, revision) VALUES(1, 0) ON CONFLICT(id) DO NOTHING;
            CREATE TRIGGER IF NOT EXISTS native_token_projection_insert_v1 AFTER INSERT ON codex_native_token_events
            BEGIN
                UPDATE codex_native_token_projection_revision SET revision = revision + 1 WHERE id = 1;
                UPDATE codex_native_accounting_revision SET revision = revision + 1 WHERE id = 1;
            END;
            CREATE TRIGGER IF NOT EXISTS native_token_projection_delete_v1 AFTER DELETE ON codex_native_token_events
            BEGIN
                UPDATE codex_native_token_projection_revision SET revision = revision + 1 WHERE id = 1;
                UPDATE codex_native_accounting_revision SET revision = revision + 1 WHERE id = 1;
            END;
            CREATE TRIGGER IF NOT EXISTS native_token_projection_update_v1 AFTER UPDATE ON codex_native_token_events
            WHEN OLD.source_event_id IS NOT NEW.source_event_id OR OLD.source_file IS NOT NEW.source_file
              OR OLD.session_id IS NOT NEW.session_id OR OLD.agent_id IS NOT NEW.agent_id
              OR OLD.observed_at_utc IS NOT NEW.observed_at_utc OR OLD.model IS NOT NEW.model
              OR OLD.reasoning_effort IS NOT NEW.reasoning_effort OR OLD.counter_epoch IS NOT NEW.counter_epoch
              OR OLD.uncached_input_tokens IS NOT NEW.uncached_input_tokens OR OLD.cache_read_tokens IS NOT NEW.cache_read_tokens
              OR OLD.cache_write_tokens IS NOT NEW.cache_write_tokens OR OLD.non_reasoning_output_tokens IS NOT NEW.non_reasoning_output_tokens
              OR OLD.reasoning_output_tokens IS NOT NEW.reasoning_output_tokens OR OLD.reported_total_tokens IS NOT NEW.reported_total_tokens
              OR OLD.captured_at_utc IS NOT NEW.captured_at_utc OR OLD.source_event_at_utc IS NOT NEW.source_event_at_utc
              OR OLD.source_timestamp_quality IS NOT NEW.source_timestamp_quality
            BEGIN
                UPDATE codex_native_token_projection_revision SET revision = revision + 1 WHERE id = 1;
                UPDATE codex_native_accounting_revision SET revision = revision + 1 WHERE id = 1;
            END;
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
