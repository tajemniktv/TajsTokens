using Microsoft.Data.Sqlite;

namespace TajsTokens.Infrastructure.Persistence;

public sealed record SqliteReadGeneration(string DatabaseIdentity, string ConnectionIdentity, long Version);

/// <summary>Process-local read-cache key. data_version is comparable only on this one
/// read-only connection; every other connection's commit invalidates it, including
/// backfills and derived writes. This is deliberately not a calibration evidence key.</summary>
public sealed class SqliteReadGenerationMonitor(string databasePath) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private string? _identity;
    private string _connectionIdentity = "";
    private bool _disposed;

    public async Task<SqliteReadGeneration?> ReadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_disposed) return null;
            string? identity = SqliteDatabaseGeneration.ReadIdentity(databasePath);
            if (identity is null) { Reset(); return null; }
            if (_identity != identity || _connection is null)
            {
                Reset();
                _connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
                await _connection.OpenAsync(token).ConfigureAwait(false);
                _identity = identity;
                _connectionIdentity = Guid.NewGuid().ToString("N");
            }
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "PRAGMA data_version;";
            long version = Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
            return new(identity, _connectionIdentity, version);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        { Reset(); return null; }
        finally { _gate.Release(); }
    }

    private void Reset() { _connection?.Dispose(); _connection = null; _identity = null; }
    public void Dispose()
    {
        _gate.Wait();
        try { _disposed = true; Reset(); }
        finally { _gate.Release(); }
    }
}
