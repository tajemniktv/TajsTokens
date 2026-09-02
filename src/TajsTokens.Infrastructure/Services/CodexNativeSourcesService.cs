using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Services;

/// <summary>
/// Read-model facade for Codex-native observations. Source-specific acquisition and row parsing
/// live in <see cref="CodexNativeSourceGateway"/> so this type remains a presentation boundary.
/// </summary>
public sealed class CodexNativeSourcesService
{
    private readonly CodexNativeSourceGateway _gateway;

    public CodexNativeSourcesService(
        string? codexHome = null,
        string? snapshotDirectory = null,
        CodexStateDbExplorerService? explorer = null,
        CodexNativeSourceGateway? gateway = null)
    {
        _gateway = gateway ?? new CodexNativeSourceGateway(codexHome, snapshotDirectory, explorer);
    }

    public CodexStateDbExplorerService Explorer => _gateway.Explorer;

    public Task<CodexNativeSourcesSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
        _gateway.ReadAsync(cancellationToken);

    public Task<CodexNativeSourcesSnapshot> InspectAsync(CancellationToken cancellationToken = default) =>
        _gateway.InspectAsync(cancellationToken);

    public Task<CodexMemorySource> ReadMemoryAsync(CancellationToken cancellationToken = default) =>
        _gateway.ReadMemoryAsync(cancellationToken);

    public Task<CodexGoalsSource> ReadGoalsAsync(CancellationToken cancellationToken = default) =>
        _gateway.ReadGoalsAsync(cancellationToken);

    public Task<CodexQueueSource> ReadQueueAsync(CancellationToken cancellationToken = default) =>
        _gateway.ReadQueueAsync(cancellationToken);

    public Task<CodexArtifactsSource> ReadArtifactsAsync(CancellationToken cancellationToken = default) =>
        _gateway.ReadArtifactsAsync(cancellationToken);

    public Task<CodexDesktopCatalogSource> ReadDesktopCatalogAsync(CancellationToken cancellationToken = default) =>
        _gateway.ReadDesktopCatalogAsync(cancellationToken);

    public Task<CodexThreadSummariesSource> ReadThreadSummariesAsync(CancellationToken cancellationToken = default) =>
        _gateway.ReadThreadSummariesAsync(cancellationToken);
}
