// Taj's Tokens | WorkspaceIdentity.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

/// <summary>
///     Stable, content-free workspace identity. A workspace may be observed before its repository is known.
/// </summary>
public sealed record WorkspaceIdentity(
    string WorkspaceId,
    string Path,
    string? RepositoryId,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc);