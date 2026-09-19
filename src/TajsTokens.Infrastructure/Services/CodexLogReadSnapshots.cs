using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Services;

/// <summary>At most two short read leases. No copies, persisted bodies or native writes.</summary>
internal sealed class CodexLogReadSnapshots
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, Lease> _leases = new();
    private sealed record Lease(string Key, DateTimeOffset Expires, SqliteConnection Connection, SqliteTransaction Transaction);

    public async Task ReleaseAsync(string id)
    {
        await _gate.WaitAsync();
        try { Remove(id); }
        finally { _gate.Release(); }
    }

    public async Task<(CodexStateRawPage Page, string? Id, DateTimeOffset? Expires)> ReadAsync(
        CodexStateDbExplorerService explorer, string path, CodexLogsQuery query, CodexLogsCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var key = System.Text.Json.JsonSerializer.Serialize(new { Path = Path.GetFullPath(path), Query = query with { PageIndex = 0, SnapshotId = null }, Capabilities = capabilities });
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RemoveExpired();
            Lease? lease = null;
            var id = query.SnapshotId;
            if (id is not null)
            {
                if (!_leases.TryGetValue(id, out lease) || lease.Key != key)
                    throw new InvalidOperationException("Log snapshot expired, was evicted, or query/source changed. Apply the query again to start a new snapshot.");
            }
            else if (query.KeepSnapshot)
            {
                var connection = await CodexStateDbExplorerService.OpenReadOnlyAsync(path, cancellationToken);
                try
                {
                    using var mode = connection.CreateCommand();
                    mode.CommandText = "PRAGMA journal_mode;";
                    if (string.Equals(Convert.ToString(await mode.ExecuteScalarAsync(cancellationToken)), "wal", StringComparison.OrdinalIgnoreCase))
                    {
                        while (_leases.Count >= 2) Remove(_leases.MinBy(x => x.Value.Expires).Key);
                        id = Guid.NewGuid().ToString("N");
                        lease = new(key, DateTimeOffset.UtcNow.AddMinutes(2), connection, connection.BeginTransaction(deferred: true));
                        _leases.Add(id, lease);
                        _ = ExpireAsync(id);
                    }
                }
                finally { if (lease is null) connection.Dispose(); }
            }
            try
            {
                var page = await explorer.ReadLogsPageAsync(path, query, capabilities, cancellationToken, lease?.Connection, lease?.Transaction);
                return (page, id, lease?.Expires);
            }
            catch { if (id is not null) Remove(id); throw; }
        }
        finally { _gate.Release(); }
    }

    private async Task ExpireAsync(string id)
    {
        await Task.Delay(TimeSpan.FromMinutes(2));
        await _gate.WaitAsync();
        try { Remove(id); }
        finally { _gate.Release(); }
    }

    private void RemoveExpired()
    {
        foreach (var id in _leases.Where(x => x.Value.Expires <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray()) Remove(id);
    }

    private void Remove(string id)
    {
        if (!_leases.Remove(id, out var lease)) return;
        try { lease.Transaction.Dispose(); }
        finally { lease.Connection.Dispose(); }
    }
}
