// Taj's Tokens | ICodexForecastDatasetReader.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Interfaces;

public interface ICodexForecastDatasetReader
{
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