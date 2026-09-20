// Taj's Tokens | ICodexQuotaProvider.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Interfaces;

public interface ICodexQuotaProvider
{
    Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken cancellationToken);

    // Compatibility for providers implementing the original snapshot-only contract. No windows
    // means unknown scope, not an inferred identity. The native adapter preserves response scope.
    async Task<CodexQuotaResponse> GetQuotaResponseAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<QuotaSnapshot> snapshots = await GetQuotaSnapshotsAsync(cancellationToken);
        string?[] accounts = snapshots.Select(snapshot => snapshot.AccountKey).Distinct().Take(2).ToArray();
        return new CodexQuotaResponse(snapshots, accounts.Length == 1 ? accounts[0] : null);
    }
}