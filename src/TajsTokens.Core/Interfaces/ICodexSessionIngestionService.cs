// Taj's Tokens | ICodexSessionIngestionService.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Interfaces;

public interface ICodexSessionIngestionService
{
    Task<CodexIngestionResult> IngestAsync(string filePath, CancellationToken cancellationToken);

    // Only an owner with a verified prefix and matching durable checkpoint may skip a source.
    Task<bool> IsUnchangedAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(false);
}
