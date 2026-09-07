using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

/// <summary>Read-only Codex-native inspection. No transcript retention or cross-source reconciliation.</summary>
public interface ICodexNativeSourcesReadModel
{
    Task<CodexNativeSourcesSnapshot> ReadAsync(CodexNativeSourcesQuery query, CancellationToken cancellationToken = default);
    Task<CodexLogsSource> ReadLogsAsync(CodexLogsQuery? query = null, CancellationToken cancellationToken = default);
}
