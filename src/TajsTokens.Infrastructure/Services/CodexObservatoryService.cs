using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Infrastructure.Services;

public sealed class CodexObservatoryService : ICodexObservatoryService
{
    private const long StateOverlapMs = 60_000;

    private readonly ICodexSessionIngestionService _ingestionService;
    private readonly ICodexObservatoryStore _store;
    private readonly CodexStateCatalog? _stateCatalog;
    private readonly SqliteCodexStateIndexStore? _stateIndexStore;
    private readonly IReadOnlyList<string> _roots;
    private bool _stateCatalogReconciledThisProcess;

    public CodexObservatoryService(
        ICodexSessionIngestionService ingestionService,
        ICodexObservatoryStore store,
        IEnumerable<string>? roots = null)
        : this(ingestionService, store, null, null, roots)
    {
    }

    internal CodexObservatoryService(
        ICodexSessionIngestionService ingestionService,
        ICodexObservatoryStore store,
        CodexStateCatalog? stateCatalog,
        SqliteCodexStateIndexStore? stateIndexStore,
        IEnumerable<string>? roots = null)
    {
        _ingestionService = ingestionService;
        _store = store;
        _stateCatalog = stateCatalog;
        _stateIndexStore = stateIndexStore;
        _roots = (roots ?? GetDefaultRoots())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<CodexObservatoryRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken);

        if (_stateCatalog is not null && _stateIndexStore is not null)
        {
            try
            {
                var indexed = await TryRefreshFromStateCatalogAsync(cancellationToken);
                if (indexed is not null)
                {
                    return indexed;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                // Codex's state database is a private optional acceleration source. Any state/index
                // incompatibility fails open to the existing rollout filesystem discovery path.
            }
        }

        return await RefreshFromFilesystemAsync(cancellationToken);
    }

    private async Task<CodexObservatoryRefreshResult?> TryRefreshFromStateCatalogAsync(
        CancellationToken cancellationToken)
    {
        if (_stateCatalog is null || _stateIndexStore is null)
        {
            return null;
        }

        await _stateIndexStore.InitializeAsync(cancellationToken);
        var watermark = await _stateIndexStore.GetWatermarkAsync(cancellationToken);

        // A full catalog read once per process catches path/archive/model metadata changes that a
        // private provider build might persist without advancing updated_at_ms. Subsequent warm
        // refreshes use the indexed timestamp window. Reading a few hundred compact SQLite rows once
        // is intentionally cheaper than trusting an undocumented timestamp as an infallible journal.
        var minimumUpdatedAtMs = _stateCatalogReconciledThisProcess
            ? Math.Max(0, watermark - StateOverlapMs)
            : 0;
        var catalog = await _stateCatalog.TryReadSinceAsync(minimumUpdatedAtMs, cancellationToken);
        if (catalog is null)
        {
            return null;
        }

        var fingerprints = await _stateIndexStore.GetFingerprintsAsync(cancellationToken);
        var changedThreads = catalog.Threads
            .Where(thread =>
                !fingerprints.TryGetValue(thread.ThreadId, out var fingerprint) ||
                !fingerprint.Matches(thread))
            .ToArray();

        var filesScanned = 0;
        var recordsScanned = 0;
        var normalized = 0;
        var errors = 0;
        long bytes = 0;
        var sessionsTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appliedThreads = new List<CodexStateThread>(changedThreads.Length);
        long? earliestFailedUpdatedAtMs = null;

        var edgesByChild = catalog.Edges
            .GroupBy(edge => edge.ChildThreadId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var thread in changedThreads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            filesScanned++;

            try
            {
                if (!File.Exists(thread.RolloutPath))
                {
                    errors++;
                    earliestFailedUpdatedAtMs = MinTimestamp(earliestFailedUpdatedAtMs, thread.UpdatedAtMs);
                    continue;
                }

                var info = new FileInfo(thread.RolloutPath);
                bytes = checked(bytes + info.Length);

                var result = await _ingestionService.IngestAsync(thread.RolloutPath, cancellationToken);
                recordsScanned += result.RecordsScanned;
                normalized += result.RecordsNormalized;

                var sourceIdentity = CodexSessionIngestionService.GetSourceIdentity(thread.RolloutPath);
                var touchedSessionId = result.SessionId ?? thread.ThreadId;
                await _store.UpsertRolloutFileAsync(
                    sourceIdentity,
                    thread.RolloutPath,
                    touchedSessionId,
                    info.Exists ? info.Length : 0,
                    DateTimeOffset.UtcNow,
                    cancellationToken);

                if (result.RecordsScanned > 0 && !string.IsNullOrWhiteSpace(touchedSessionId))
                {
                    sessionsTouched.Add(touchedSessionId);
                }

                if (edgesByChild.TryGetValue(thread.ThreadId, out var edges))
                {
                    var linkedAt = FromUnixMillisecondsOrEpoch(thread.CreatedAtMs);
                    foreach (var edge in edges)
                    {
                        await _store.UpsertAgentRelationshipAsync(
                            new AgentRelationship(edge.ParentThreadId, edge.ChildThreadId, linkedAt),
                            cancellationToken);
                    }
                }

                appliedThreads.Add(thread);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (File.Exists(thread.RolloutPath))
            {
                errors++;
                earliestFailedUpdatedAtMs = MinTimestamp(earliestFailedUpdatedAtMs, thread.UpdatedAtMs);
            }
        }

        var nextWatermark = earliestFailedUpdatedAtMs is long failedAt
            ? Math.Max(0, failedAt - StateOverlapMs)
            : Math.Max(watermark, catalog.MaxUpdatedAtMs);

        if (appliedThreads.Count > 0 || nextWatermark != watermark)
        {
            await _stateIndexStore.CommitAsync(appliedThreads, nextWatermark, cancellationToken);
        }

        // Reconciliation is considered complete only after the full catalog path reached its normal
        // return. A failed/cancelled first pass retries the full comparison on the next refresh.
        if (minimumUpdatedAtMs == 0 && earliestFailedUpdatedAtMs is null)
        {
            _stateCatalogReconciledThisProcess = true;
        }

        return new CodexObservatoryRefreshResult(
            catalog.TotalThreadCount,
            filesScanned,
            recordsScanned,
            normalized,
            sessionsTouched.Count,
            errors,
            bytes);
    }

    private async Task<CodexObservatoryRefreshResult> RefreshFromFilesystemAsync(
        CancellationToken cancellationToken)
    {
        var files = DiscoverFiles();
        var filesScanned = 0;
        var recordsScanned = 0;
        var normalized = 0;
        var errors = 0;
        long bytes = 0;
        var sessionsTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            filesScanned++;
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists)
                {
                    continue;
                }

                bytes = checked(bytes + info.Length);
                var sourceIdentity = CodexSessionIngestionService.GetSourceIdentity(file);
                var filenameSessionId = CodexRolloutParser.ExtractSessionIdFromFileName(file);

                // The fallback path retains the conservative pre-ingestion identity refresh because
                // it has no provider-owned catalog telling us whether an EOF file moved/archived.
                await _store.UpsertRolloutFileAsync(
                    sourceIdentity,
                    file,
                    filenameSessionId,
                    info.Length,
                    DateTimeOffset.UtcNow,
                    cancellationToken);

                var result = await _ingestionService.IngestAsync(file, cancellationToken);
                recordsScanned += result.RecordsScanned;
                normalized += result.RecordsNormalized;

                var touchedSessionId = result.SessionId ?? filenameSessionId;
                if (result.RecordsScanned > 0 && !string.IsNullOrWhiteSpace(touchedSessionId))
                {
                    sessionsTouched.Add(touchedSessionId);
                }

                await _store.UpsertRolloutFileAsync(
                    sourceIdentity,
                    file,
                    touchedSessionId,
                    info.Exists ? info.Length : 0,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (File.Exists(file))
            {
                // One malformed/locked/replaced rollout must not stop the remaining local corpus.
                errors++;
            }
        }

        return new CodexObservatoryRefreshResult(
            files.Count,
            filesScanned,
            recordsScanned,
            normalized,
            sessionsTouched.Count,
            errors,
            bytes);
    }

    private IReadOnlyList<string> DiscoverFiles()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var root in _roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", options))
                {
                    files.Add(Path.GetFullPath(file));
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Root itself may become inaccessible between the existence check and enumeration.
            }
            catch (DirectoryNotFoundException)
            {
                // Directory can disappear while Codex rotates or archives state.
            }
            catch (IOException)
            {
                // Discovery is retried on the next normal coordinator refresh.
            }
        }

        return files
            .OrderByDescending(file =>
            {
                try { return File.GetLastWriteTimeUtc(file); }
                catch { return DateTime.MinValue; }
            })
            .ToArray();
    }

    private static IEnumerable<string> GetDefaultRoots()
    {
        var codexHome = GetCodexHome();
        yield return Path.Combine(codexHome, "sessions");
        yield return Path.Combine(codexHome, "archived_sessions");
        yield return Path.Combine(codexHome, "archive");
    }

    internal static string GetCodexHome()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        return Path.GetFullPath(codexHome);
    }

    private static long? MinTimestamp(long? current, long candidate) =>
        current is null ? candidate : Math.Min(current.Value, candidate);

    private static DateTimeOffset FromUnixMillisecondsOrEpoch(long milliseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }
}
