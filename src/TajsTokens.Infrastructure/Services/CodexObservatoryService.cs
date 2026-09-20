// Taj's Tokens | CodexObservatoryService.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;

#endregion

namespace TajsTokens.Infrastructure.Services;

public sealed class CodexObservatoryService : ICodexObservatoryService, ICodexRolloutInspection
{
    private const long StateOverlapMs = 60_000;
    private const long StateRolloutVisibilityToleranceMs = 250;
    private static readonly TimeSpan StateReconciliationInterval = TimeSpan.FromMinutes(5);

    private readonly ICodexSessionIngestionService _ingestionService;
    private readonly IReadOnlyList<string> _roots;
    private readonly CodexStateCatalog? _stateCatalog;
    private readonly SqliteCodexStateIndexStore? _stateIndexStore;
    private readonly ICodexObservatoryStore _store;
    private readonly TimeProvider _timeProvider;
    private int _backfillOffset;
    private CodexCollectionCoverage? _lastCoverage;
    private string? _lastStateCatalogPath;
    private long? _lastStateReconciliation;

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
        IEnumerable<string>? roots = null,
        TimeProvider? timeProvider = null)
    {
        _ingestionService = ingestionService;
        _store = store;
        _stateCatalog = stateCatalog;
        _stateIndexStore = stateIndexStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
                CodexObservatoryRefreshResult? indexed = await TryRefreshFromStateCatalogAsync(cancellationToken);
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
                exception is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException
                    or InvalidDataException or
                    InvalidCastException or FormatException or OverflowException or ArgumentException or NotSupportedException)
            {
                // Codex's state database is a private optional acceleration source. Any state/index
                // incompatibility fails open to the existing rollout filesystem discovery path.
            }
        }

        return await RefreshFromFilesystemAsync(cancellationToken);
    }

    public async Task<CodexRolloutInspectionReport> InspectAlternateRolloutsAsync(int offset, CancellationToken cancellationToken)
    {
        offset = Math.Max(0, offset);
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        // Separate read-only inspection: no ingestion, checkpoint, cursor, or cached-coverage writes.
        CodexStateCatalogBatch? catalog = _stateCatalog is null ? null : await _stateCatalog.TryReadSinceAsync(0, cancellationToken);
        if (catalog is null)
            return new CodexRolloutInspectionReport(
                observedAt,
                null,
                null,
                0,
                [],
                "No usable selected state catalog. Alternate-path ownership and the unindexed path count are unknown.");

        HashSet<string> indexed = catalog.Threads.Select(thread => thread.RolloutPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] alternates = DiscoverFiles(cancellationToken).Where(path => !indexed.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        Dictionary<string, CodexStateThread[]> owners = catalog.Threads.GroupBy(thread => thread.ThreadId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var comparisons = new List<CodexRolloutComparison>();
        var reader = new CodexRolloutComparisonReader();
        foreach (string path in alternates.Skip(offset).Take(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? id = CodexRolloutParser.ExtractSessionIdFromFileName(path);
            if (id is null || !owners.TryGetValue(id, out CodexStateThread[]? matches) || matches.Length != 1)
            {
                comparisons.Add(
                    new CodexRolloutComparison(
                        path,
                        null,
                        CodexRolloutComparisonKind.Unresolved,
                        false,
                        false,
                        null,
                        "No unique indexed counterpart from filename identity. A first session_meta may be a copied non-owning prefix; ownership is not guessed."));
                continue;
            }
            comparisons.Add(await reader.CompareAsync(path, matches[0].RolloutPath, matches[0].ThreadId, cancellationToken));
        }
        return new CodexRolloutInspectionReport(
            observedAt,
            catalog.DatabasePath,
            alternates.Length,
            offset,
            comparisons,
            "Read-only sample: up to 8 pairs, 2 MiB and 20,000 records per file. Native paths outside discovery roots are compared when indexed. " +
            "Filesystem discovery and cross-file reads are best-effort, not an atomic snapshot. Results describe this inspection only; no import, merge, export, or durable content copy occurs.");
    }

    private async Task<CodexObservatoryRefreshResult?> TryRefreshFromStateCatalogAsync(
        CancellationToken cancellationToken)
    {
        if (_stateCatalog is null || _stateIndexStore is null)
        {
            return null;
        }

        await _stateIndexStore.InitializeAsync(cancellationToken);
        long watermark = await _stateIndexStore.GetWatermarkAsync(cancellationToken);

        // updated_at_ms is an acceleration hint, not a complete change journal (Codex can update
        // rollout_path alone). Reconcile compact catalog fingerprints periodically even in a
        // long-running process; unchanged rollout bodies remain unopened. Use monotonic elapsed time.
        long reconciliationStarted = _timeProvider.GetTimestamp();
        bool reconciliationDue = _lastStateReconciliation is not long lastReconciliation ||
                                 _timeProvider.GetElapsedTime(lastReconciliation, reconciliationStarted) >= StateReconciliationInterval;
        long minimumUpdatedAtMs = !reconciliationDue
            ? Math.Max(0, watermark - StateOverlapMs)
            : 0;
        CodexStateCatalogBatch? catalog = await _stateCatalog.TryReadSinceAsync(minimumUpdatedAtMs, cancellationToken);
        if (catalog is null)
        {
            return null;
        }

        // A new source generation can have timestamps older than the previous database's cursor.
        // Reread without that cursor before comparing fingerprints; never clear retained evidence.
        if (minimumUpdatedAtMs != 0 &&
            !string.Equals(_lastStateCatalogPath, catalog.DatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            minimumUpdatedAtMs = 0;
            catalog = await _stateCatalog.TryReadSinceAsync(0, cancellationToken);
            if (catalog is null) return null;
        }

        reconciliationDue |= !string.Equals(_lastStateCatalogPath, catalog.DatabasePath, StringComparison.OrdinalIgnoreCase);
        if (reconciliationDue)
        {
            HashSet<string> discovered = DiscoverFiles(cancellationToken).ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> indexed = catalog.Threads.Select(thread => thread.RolloutPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _lastCoverage = new CodexCollectionCoverage(
                _timeProvider.GetUtcNow(),
                indexed.Count,
                indexed.Count(File.Exists),
                discovered.Count,
                discovered.Count(path => !indexed.Contains(path)),
                indexed.Count(path => !discovered.Contains(path)));
        }

        IReadOnlyDictionary<string, CodexStateThreadFingerprint> fingerprints =
            await _stateIndexStore.GetFingerprintsAsync(cancellationToken);
        CodexStateThread[] changedThreads = catalog.Threads
            .Where(thread =>
                !fingerprints.TryGetValue(thread.ThreadId, out CodexStateThreadFingerprint? fingerprint) ||
                !fingerprint.Matches(thread))
            .ToArray();

        // Bound indexed refreshes so an upgrade's old corpus cannot hold recent captures behind a
        // whole-history pass. Reserve half the batch for rotating older work, including retries.
        // This is a file-count bound, not a latency guarantee for a single large/slow file.
        CodexStateThread[] recent = changedThreads.OrderByDescending(x => x.UpdatedAtMs)
            .ThenBy(x => x.ThreadId, StringComparer.Ordinal).Take(8).ToArray();
        CodexStateThread[] older = changedThreads.Except(recent).OrderBy(x => x.UpdatedAtMs)
            .ThenBy(x => x.ThreadId, StringComparer.Ordinal).ToArray();
        int offset = older.Length == 0 ? 0 : _backfillOffset % older.Length;
        CodexStateThread[] batch = recent.Concat(older.Skip(offset).Concat(older.Take(offset)).Take(8)).ToArray();
        _backfillOffset = older.Length == 0 ? 0 : (offset + Math.Min(8, older.Length)) % older.Length;
        CodexStateThread[] deferred = changedThreads.Except(batch).ToArray();

        // Edge persistence is intentionally independent of rollout selection. Codex can insert a
        // spawn edge without changing the child's thread fingerprint, and relationships are cheap to
        // reconcile against the already-normalized TajsTokens graph.
        await ReconcileSpawnEdgesAsync(catalog.Edges, cancellationToken);

        int filesScanned = 0;
        int recordsScanned = 0;
        int normalized = 0;
        int errors = 0;
        long bytes = 0;
        var sessionsTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appliedThreads = new List<CodexStateThread>(changedThreads.Length);
        long? earliestUnappliedUpdatedAtMs = deferred.Length == 0 ? null : deferred.Min(x => x.UpdatedAtMs);

        foreach (CodexStateThread thread in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            filesScanned++;
            fingerprints.TryGetValue(thread.ThreadId, out CodexStateThreadFingerprint? previousFingerprint);

            try
            {
                if (!File.Exists(thread.RolloutPath))
                {
                    errors++;
                    earliestUnappliedUpdatedAtMs = MinTimestamp(earliestUnappliedUpdatedAtMs, thread.UpdatedAtMs);
                    continue;
                }

                var infoBefore = new FileInfo(thread.RolloutPath);
                bytes = checked(bytes + infoBefore.Length);

                CodexIngestionResult result = await _ingestionService.IngestAsync(thread.RolloutPath, cancellationToken);
                recordsScanned += result.RecordsScanned;
                normalized += result.RecordsNormalized;

                var infoAfter = new FileInfo(thread.RolloutPath);
                infoAfter.Refresh();
                string sourceIdentity = result.SourceIdentity ?? CodexSessionIngestionService.GetSourceIdentity(thread.RolloutPath);
                string touchedSessionId = result.SessionId ?? thread.ThreadId;
                await _store.UpsertRolloutFileAsync(
                    sourceIdentity,
                    thread.RolloutPath,
                    touchedSessionId,
                    infoAfter.Exists ? infoAfter.Length : 0,
                    DateTimeOffset.UtcNow,
                    cancellationToken);

                if (result.RecordsScanned > 0 && !string.IsNullOrWhiteSpace(touchedSessionId))
                {
                    sessionsTouched.Add(touchedSessionId);
                }

                if (!await CanCommitFingerprintAsync(
                        thread,
                        previousFingerprint,
                        result,
                        infoAfter,
                        cancellationToken))
                {
                    // State and rollout are separate provider persistence surfaces. If state appears
                    // ahead of the authoritative rollout, keep the thread inside the overlap window
                    // and retry rather than permanently teaching the fast path to skip it.
                    earliestUnappliedUpdatedAtMs = MinTimestamp(earliestUnappliedUpdatedAtMs, thread.UpdatedAtMs);
                    continue;
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
                earliestUnappliedUpdatedAtMs = MinTimestamp(earliestUnappliedUpdatedAtMs, thread.UpdatedAtMs);
            }
        }

        long nextWatermark = earliestUnappliedUpdatedAtMs is long unappliedAt
            ? Math.Max(0, unappliedAt - StateOverlapMs)
            : minimumUpdatedAtMs == 0
                ? catalog.MaxUpdatedAtMs
                : Math.Max(watermark, catalog.MaxUpdatedAtMs);

        if (appliedThreads.Count > 0 || nextWatermark != watermark)
        {
            await _stateIndexStore.CommitAsync(appliedThreads, nextWatermark, cancellationToken);
        }

        // Failed/cancelled full passes do not defer reconciliation: retry on the next refresh.
        if (reconciliationDue && earliestUnappliedUpdatedAtMs is null)
        {
            _lastStateReconciliation = reconciliationStarted;
            _lastStateCatalogPath = catalog.DatabasePath;
        }

        return new CodexObservatoryRefreshResult(
            catalog.TotalThreadCount,
            filesScanned,
            recordsScanned,
            normalized,
            sessionsTouched.Count,
            errors,
            bytes) { Coverage = _lastCoverage, DeferredFiles = deferred.Length };
    }

    private async Task<bool> CanCommitFingerprintAsync(
        CodexStateThread thread,
        CodexStateThreadFingerprint? previousFingerprint,
        CodexIngestionResult result,
        FileInfo fileInfo,
        CancellationToken cancellationToken)
    {
        if (!fileInfo.Exists || result.LastCompleteRecordOffset < fileInfo.Length)
        {
            return false;
        }

        // A path/archive/model/reasoning correction that did not change the thread activity/token
        // cursor is provider metadata, not evidence that a rollout event is still pending.
        if (previousFingerprint is not null &&
            previousFingerprint.UpdatedAtMs == thread.UpdatedAtMs &&
            previousFingerprint.TokensUsed == thread.TokensUsed &&
            !previousFingerprint.CatalogMetadataMatches(thread))
        {
            return true;
        }

        long? persistedCounterTotal = await _stateIndexStore!.GetPersistedCounterTotalAsync(
            thread.ThreadId,
            cancellationToken);
        if (persistedCounterTotal == thread.TokensUsed)
        {
            return true;
        }

        // Some valid Codex threads have a state-reported cumulative total without a parseable
        // token_count in the retained rollout. In that degraded-coverage case, accept the fingerprint
        // only when the rollout itself has reached EOF and its write timestamp has caught up to the
        // state update. The tolerance covers the observed few-millisecond state-vs-rollout skew.
        DateTime stateUpdatedUtc = FromUnixMillisecondsOrEpoch(thread.UpdatedAtMs).UtcDateTime;
        return fileInfo.LastWriteTimeUtc >= stateUpdatedUtc.AddMilliseconds(-StateRolloutVisibilityToleranceMs);
    }

    private async Task ReconcileSpawnEdgesAsync(
        IReadOnlyList<CodexStateEdge> edges,
        CancellationToken cancellationToken)
    {
        if (edges.Count == 0)
        {
            return;
        }

        IReadOnlyList<AgentRelationship> existing = await _store.GetAgentRelationshipsAsync(cancellationToken);
        var known = new HashSet<string>(
            existing.Select(relationship => RelationshipKey(
                relationship.ParentAgentId,
                relationship.ChildAgentId)),
            StringComparer.OrdinalIgnoreCase);

        foreach (CodexStateEdge edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!known.Add(RelationshipKey(edge.ParentThreadId, edge.ChildThreadId)))
            {
                continue;
            }

            await _store.UpsertAgentRelationshipAsync(
                new AgentRelationship(
                    edge.ParentThreadId,
                    edge.ChildThreadId,
                    FromUnixMillisecondsOrEpoch(edge.ChildCreatedAtMs)),
                cancellationToken);
        }
    }

    private async Task<CodexObservatoryRefreshResult> RefreshFromFilesystemAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> files = DiscoverFiles(cancellationToken);
        int filesScanned = 0;
        int recordsScanned = 0;
        int normalized = 0;
        int errors = 0;
        long bytes = 0;
        var sessionsTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
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
                string? filenameSessionId = CodexRolloutParser.ExtractSessionIdFromFileName(file);

                CodexIngestionResult result = await _ingestionService.IngestAsync(file, cancellationToken);
                string sourceIdentity = result.SourceIdentity ?? CodexSessionIngestionService.GetSourceIdentity(file);
                recordsScanned += result.RecordsScanned;
                normalized += result.RecordsNormalized;

                string? touchedSessionId = result.SessionId ?? filenameSessionId;
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

    private IReadOnlyList<string> DiscoverFiles(CancellationToken cancellationToken = default)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (string root in _roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*.jsonl", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
                try
                {
                    return File.GetLastWriteTimeUtc(file);
                }
                catch
                {
                    return DateTime.MinValue;
                }
            })
            .ToArray();
    }

    private static IEnumerable<string> GetDefaultRoots()
    {
        string codexHome = GetCodexHome();
        yield return Path.Combine(codexHome, "sessions");
        yield return Path.Combine(codexHome, "archived_sessions");
        yield return Path.Combine(codexHome, "archive");
    }

    internal static string GetCodexHome()
    {
        string? codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        return Path.GetFullPath(codexHome);
    }

    private static string RelationshipKey(string parent, string child)
    {
        return $"{parent}\0{child}";
    }

    private static long? MinTimestamp(long? current, long candidate)
    {
        return current is null ? candidate : Math.Min(current.Value, candidate);
    }

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