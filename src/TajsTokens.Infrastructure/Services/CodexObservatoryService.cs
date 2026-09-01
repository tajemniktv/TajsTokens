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
        var scanned = 0;
        var normalized = 0;
        var errors = 0;
        long bytes = 0;
        var sessionsTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            try
            {
                var info = new FileInfo(file);
                if (info.Exists)
                {
                    bytes = checked(bytes + info.Length);
                }

                var count = await _ingestionService.IngestAsync(file, cancellationToken);
                normalized += count;
                if (count > 0)
                {
                    var sessionId = CodexRolloutParser.ExtractSessionIdFromFileName(file);
                    if (!string.IsNullOrWhiteSpace(sessionId))
                    {
                        sessionsTouched.Add(sessionId);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (File.Exists(file))
            {
                // One malformed/locked/replaced rollout must not stop the remaining local corpus.
                // Detailed provider diagnostics are intentionally content-free and surface only the
                // aggregate error count at this layer; the parser never logs payload text.
                errors++;
            }
        }

        return new CodexObservatoryRefreshResult(files.Count, scanned, normalized, sessionsTouched.Count, errors, bytes);
    }

    private IReadOnlyList<string> DiscoverFiles()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
                {
                    files.Add(Path.GetFullPath(file));
                }
            }
            catch (UnauthorizedAccessException)
            {
                // A single inaccessible subtree should not make the whole Codex home unavailable.
            }
            catch (DirectoryNotFoundException)
            {
                // Directory can disappear while Codex rotates/archives state.
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
