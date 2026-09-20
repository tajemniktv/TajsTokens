// Taj's Tokens | ICodexThreadReadModel.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Interfaces;

/// <summary>
///     Authoritative provider-native Codex thread read model consumed by product surfaces. Source
///     acquisition and SQLite inspection remain behind this boundary.
/// </summary>
public interface ICodexThreadReadModel
{
    Task<CodexThreadNavigationResult> BrowseThreadsAsync(
        CodexThreadNavigationQuery query,
        CancellationToken cancellationToken);

    Task<CodexThreadSearchResult> SearchThreadsAsync(
        string? search,
        int take,
        CancellationToken cancellationToken);

    Task<CodexThreadReadResult> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken);
}