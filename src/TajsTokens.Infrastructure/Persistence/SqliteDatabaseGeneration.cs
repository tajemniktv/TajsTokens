using TajsTokens.Infrastructure.Ingestion;
using Microsoft.Data.Sqlite;

namespace TajsTokens.Infrastructure.Persistence;

/// <summary>Initialization reuse is local to one physical app-owned database. Unknown identity
/// disables reuse; replacement/deletion must not inherit an earlier schema initialization.</summary>
internal sealed class SqliteDatabaseGeneration(string path)
{
    public string? Identity { get; private set; }
    public bool IsCurrent => Identity is { } identity && ReadIdentity(path) == identity;
    public void MarkInitialized() => Identity = ReadIdentity(path);

    public void Reopen(string connectionString)
    {
        // Idle pooled native handles can still refer to the previous physical file.
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    public static string? ReadIdentity(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string identity = CodexSessionIngestionService.GetSourceIdentity(path);
            return identity.StartsWith("fallback", StringComparison.Ordinal) ? null : identity;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }
}
