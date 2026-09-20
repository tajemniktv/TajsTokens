// Taj's Tokens | ICodexServerEvidenceProvider.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Interfaces;

public interface ICodexServerEvidenceProvider
{
    Task<CodexServerCollection> CollectAsync(IReadOnlyList<string> threadIds, CancellationToken cancellationToken);
}