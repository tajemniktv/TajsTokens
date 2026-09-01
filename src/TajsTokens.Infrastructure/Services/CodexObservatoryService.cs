using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;

namespace TajsTokens.Infrastructure.Services;

public sealed class CodexObservatoryService : ICodexObservatoryService
{
    private readonly ICodexSessionIngestionService _ingestionService;
    private readonly ICodexObservatoryStore _store;
    private readonly IReadOnlyList<string> _roots;

    public CodexObservatoryService(
        ICodexSessionIngestionService ingestionService,
        ICodexObservatoryStore store,
        IEnumerable<string>? roots = null)
    {
        _ingestionService = ingestionService;
        _store = store;
        _roots = (roots ?? GetDefaultRoots())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<CodexObservatoryRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken);

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

                // Refresh file identity even at EOF. This lets an archive move update the current path
                // without requiring a new JSONL record to appear first.
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
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        yield return Path.Combine(codexHome, "sessions");
        yield return Path.Combine(codexHome, "archived_sessions");
        yield return Path.Combine(codexHome, "archive");
    }
}
