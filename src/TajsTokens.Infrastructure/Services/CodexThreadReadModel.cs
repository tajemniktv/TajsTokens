using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Services;

/// <summary>
/// Product-facing Codex thread query boundary. The source reader remains an acquisition detail;
/// callers receive the provider-native normalized read model and never depend on the SQLite
/// explorer directly.
/// </summary>
public sealed class CodexThreadReadModel : ICodexThreadReadModel
{
    private readonly CodexThreadObservabilityService _sourceReader;

    public CodexThreadReadModel(CodexThreadObservabilityService sourceReader)
    {
        _sourceReader = sourceReader ?? throw new ArgumentNullException(nameof(sourceReader));
    }

    public Task<CodexThreadNavigationResult> BrowseThreadsAsync(
        CodexThreadNavigationQuery query,
        CancellationToken cancellationToken) =>
        _sourceReader.BrowseThreadsAsync(query, cancellationToken);

    public Task<CodexThreadSearchResult> SearchThreadsAsync(
        string? search,
        int take,
        CancellationToken cancellationToken) =>
        _sourceReader.SearchThreadsAsync(search, take, cancellationToken);

    public async Task<CodexThreadReadResult> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken)
    {
        var sourceResult = await _sourceReader.ReadThreadAsync(threadId, cancellationToken);
        if (sourceResult.StateSources.Count == 0)
        {
            return sourceResult;
        }

        // Re-apply the presentation policy at the product boundary. This keeps the source reader
        // useful for investigation while making the UI contract authoritative and deterministic.
        var state = CodexThreadReadModelPolicy.Reconcile(sourceResult.StateSources);
        return sourceResult with
        {
            Project = state.PreferredProject,
            Section = state.PreferredSection,
            DynamicTools = state.PreferredDynamicTools,
            SpawnEdges = state.PreferredSpawnEdges,
            StateReadModel = state,
            StateSourceSelectionRationale = string.IsNullOrWhiteSpace(sourceResult.StateSourceSelectionRationale)
                ? state.SelectionRationale
                : $"{sourceResult.StateSourceSelectionRationale} Optional state: {state.SelectionRationale}"
        };
    }
}
