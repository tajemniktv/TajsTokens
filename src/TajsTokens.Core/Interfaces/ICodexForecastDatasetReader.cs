// Taj's Tokens | ICodexForecastDatasetReader.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Interfaces;

public interface ICodexForecastDatasetReader
{
    // A presentation read may omit predictive inputs while preserving ledger/quota semantics.
    // Readers without a specialized projection retain the complete-read fallback.
    Task<CodexForecastDataset> ReadLedgerAndQuotaAsync(
        string provider,
        string profile,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken,
        string? accountKey = null,
        bool includeQuota = true)
        => ReadAsync(provider, profile, fromUtc, toUtc, cancellationToken, accountKey, includeQuota, includeLedger: true);

    Task<CodexForecastDataset> ReadAsync(
        string provider,
        string profile,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken,
        string? accountKey = null,
        bool includeQuota = true,
        bool includeLedger = false);
}
