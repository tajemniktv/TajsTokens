namespace TajsTokens.Core.Tests;

internal static class LegacyIntelligenceFixture
{
    // A downgraded version marker alone is not an old schema. Remove v7 derived structures too.
    public const string RemoveResetTails = """
        DROP TRIGGER invalidate_reset_cache_after_quota_insert;
        DROP TRIGGER invalidate_reset_cache_after_quota_update;
        DROP INDEX idx_quota_snapshots_reset_tail;
        DROP TABLE quota_reset_streams;
        ALTER TABLE quota_reset_cache_state DROP COLUMN pending_rows;
        ALTER TABLE quota_reset_cache_state DROP COLUMN last_event_at_utc;
        ALTER TABLE quota_reset_cache_state DROP COLUMN next_future_at_utc;
        ALTER TABLE quota_reset_cache_state DROP COLUMN refreshed_at_utc;
        """;
}
