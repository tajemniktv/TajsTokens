using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

internal static class SqliteCodexNativeTokenCoverage
{
    // Keep this classification identical to the dated readers: neither capture nor legacy observed time substitutes for source time.
    internal const string Columns = """
        COUNT(*),
        COALESCE(SUM(CASE WHEN source_timestamp_quality='Valid' AND source_event_at_utc IS NOT NULL THEN 1 ELSE 0 END),0),
        COALESCE(SUM(CASE WHEN source_timestamp_quality='Valid' AND source_event_at_utc IS NOT NULL THEN reported_total_tokens ELSE 0 END),0),
        COALESCE(SUM(CASE WHEN source_timestamp_quality='Valid' AND source_event_at_utc IS NOT NULL THEN 0 ELSE 1 END),0),
        COALESCE(SUM(CASE WHEN source_timestamp_quality='Valid' AND source_event_at_utc IS NOT NULL THEN 0 ELSE reported_total_tokens END),0),
        COALESCE(SUM(CASE WHEN NULLIF(trim(model),'') IS NULL THEN 1 ELSE 0 END),0),
        COALESCE(SUM(CASE WHEN NULLIF(trim(model),'') IS NULL THEN reported_total_tokens ELSE 0 END),0)
        """;

    internal static CodexNativeTokenCoverage Read(SqliteDataReader reader, int first) => new(
        reader.GetInt64(first), reader.GetInt64(first + 1), reader.GetInt64(first + 2), reader.GetInt64(first + 3),
        reader.GetInt64(first + 4), reader.GetInt64(first + 5), reader.GetInt64(first + 6));
}
