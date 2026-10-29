using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;

namespace TajsTokens.Infrastructure.Persistence;

/// <summary>Bounded, disposable derived artifacts, separate from retained telemetry evidence.</summary>
public sealed class SqliteRidgeFitCache : IRidgeFitCache, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, RidgeFitParameters> _memory = new(StringComparer.Ordinal);
    private readonly int _maxEntries;
    private readonly int _admissionStride;
    private readonly object _sync = new();
    private bool _disabled;
    private int _writes;
    public long Hits { get; private set; }
    public long Misses { get; private set; }

    public SqliteRidgeFitCache(string path, int maxEntries = 16384)
        : this(path, maxEntries, 8192) { }

    // Historical replay exceeds the entry budget. A stable disk subset avoids a
    // sequential scan evicting every useful fit before the next slice reaches it.
    // All completed fits still enter the same bounded in-process cache.
    internal static SqliteRidgeFitCache ForCalibration(string path) => new(path, 16384, 8192, 4);

    internal SqliteRidgeFitCache(string path, int maxEntries, int maxPages, int admissionStride = 1)
    {
        if (maxEntries is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (admissionStride is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(admissionStride));
        _maxEntries = maxEntries;
        _admissionStride = admissionStride;
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, DefaultTimeout = 1 }.ToString());
        try
        {
            _connection.Open();
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            bool wal = string.Equals(command.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase);
            // Fits are rebuildable, checksum-validated artifacts. In WAL mode NORMAL keeps
            // transactions atomic without forcing every fit to storage. A power failure may
            // lose recent fits; source evidence uses its separate, unchanged connection policy.
            command.CommandText = $"""
                {(wal ? "PRAGMA synchronous=NORMAL;" : "")}
                PRAGMA journal_size_limit=1048576;
                PRAGMA max_page_count={maxPages};
                CREATE TABLE IF NOT EXISTS ridge_fits_v1 (key TEXT PRIMARY KEY, payload TEXT NOT NULL, checksum TEXT NOT NULL);
                """;
            command.ExecuteNonQuery();
        }
        catch (SqliteException) { _disabled = true; }
    }

    public RidgeFitParameters? Read(string key)
    {
        lock (_sync) return ReadCore(key);
    }

    private RidgeFitParameters? ReadCore(string key)
    {
        if (_memory.TryGetValue(key, out RidgeFitParameters? fit)) { Hits++; return Clone(fit); }
        if (!_disabled)
        {
            try
            {
                using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = "SELECT payload,checksum FROM ridge_fits_v1 WHERE key=$key AND length(payload)<=16384;";
                command.Parameters.AddWithValue("$key", key);
                using SqliteDataReader reader = command.ExecuteReader();
                if (reader.Read() && reader.GetString(0) is { } payload &&
                    Checksum(payload) == reader.GetString(1) &&
                    JsonSerializer.Deserialize<RidgeFitParameters>(payload) is { } stored && Valid(stored))
                {
                    Remember(key, stored);
                    Hits++;
                    return Clone(stored);
                }
            }
            catch (Exception error) when (error is SqliteException or JsonException or NotSupportedException) { }
        }
        Misses++;
        return null;
    }

    public void Write(string key, RidgeFitParameters fit)
    {
        lock (_sync) WriteCore(key, fit);
    }

    private void WriteCore(string key, RidgeFitParameters fit)
    {
        if (!Valid(fit)) return;
        string payload = JsonSerializer.Serialize(fit);
        if (payload.Length > 16384) return;
        Remember(key, fit);
        if (_disabled) return;
        if (_admissionStride > 1 && BinaryPrimitives.ReadUInt32LittleEndian(
                SHA256.HashData(Encoding.UTF8.GetBytes(key))) % _admissionStride != 0) return;
        try
        {
            using SqliteCommand command = _connection.CreateCommand();
            // Each fit is an atomic cache entry: killing a bounded worker never loses source data
            // or makes an unfinished model look complete. Admitted fits survive to later slices.
            int batch = Math.Min(64, _maxEntries);
            if (_writes++ % batch == 0)
            {
                command.CommandText = "DELETE FROM ridge_fits_v1 WHERE rowid IN (SELECT rowid FROM ridge_fits_v1 ORDER BY rowid LIMIT MAX(0,(SELECT COUNT(*) FROM ridge_fits_v1)-$bound));";
                command.Parameters.AddWithValue("$bound", Math.Max(0, _maxEntries - batch));
                command.ExecuteNonQuery();
                command.Parameters.Clear();
            }
            command.CommandText = "INSERT OR REPLACE INTO ridge_fits_v1(key,payload,checksum) VALUES($key,$payload,$checksum);";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$checksum", Checksum(payload));
            command.ExecuteNonQuery();
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 13)
        {
            // Large fits can reach the byte bound before the row bound. Free old entries so
            // subsequent fits still persist instead of permanently freezing a full cache.
            try
            {
                using SqliteCommand prune = _connection.CreateCommand();
                prune.CommandText = "DELETE FROM ridge_fits_v1 WHERE rowid IN (SELECT rowid FROM ridge_fits_v1 ORDER BY rowid LIMIT MAX(1,(SELECT COUNT(*) FROM ridge_fits_v1)/4));";
                prune.ExecuteNonQuery();
            }
            catch (SqliteException) { _disabled = true; }
        }
        catch (SqliteException) { _disabled = true; }
    }

    private void Remember(string key, RidgeFitParameters fit)
    {
        if (_memory.Count >= 256) _memory.Clear();
        _memory[key] = Clone(fit);
    }

    private static RidgeFitParameters Clone(RidgeFitParameters fit) => new(
        (double[])fit.Means.Clone(), (double[])fit.Scales.Clone(), (double[])fit.Coefficients.Clone());

    private static bool Valid(RidgeFitParameters fit) => fit.Means is { Length: <= 512 } && fit.Scales is not null &&
        fit.Coefficients is not null && fit.Scales.Length == fit.Means.Length && fit.Coefficients.Length == fit.Means.Length + 1 &&
        fit.Means.All(double.IsFinite) && fit.Scales.All(x => double.IsFinite(x) && x > 0) && fit.Coefficients.All(double.IsFinite);

    private static string Checksum(string payload) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    public void Dispose() { lock (_sync) { _disabled = true; _connection.Dispose(); } }
}
