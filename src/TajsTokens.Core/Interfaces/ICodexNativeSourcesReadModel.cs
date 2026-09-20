// Taj's Tokens | ICodexNativeSourcesReadModel.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Interfaces;

/// <summary>Read-only Codex-native inspection. No transcript retention or cross-source reconciliation.</summary>
public interface ICodexNativeSourcesReadModel
{
    Task<CodexNativeSourcesSnapshot> ReadAsync(CodexNativeSourcesQuery query, CancellationToken cancellationToken = default);
    Task<CodexLogsSource> ReadLogsAsync(CodexLogsQuery? query = null, CancellationToken cancellationToken = default);
    Task ReleaseLogsSnapshotAsync(string id);
}