// Taj's Tokens | ICodexServerEvidenceReader.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Interfaces;

public sealed record CodexServerEvidenceHistory(
    IReadOnlyList<CodexServerObservation> Observations,
    bool IsPartial,
    int InvalidCaptures,
    DateTimeOffset CapturedAtUtc);

public interface ICodexServerEvidenceReader
{
    Task<CodexServerEvidenceHistory> ReadHistoryAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset asOf,
        string? accountKey,
        CancellationToken token);
}