namespace TajsTokens.Core.Models;

/// <summary>
/// Stable, content-free workspace identity. A workspace may be observed before its repository is known.
/// </summary>
public sealed record WorkspaceIdentity(
    string WorkspaceId,
    string Path,
    string? RepositoryId,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc);
