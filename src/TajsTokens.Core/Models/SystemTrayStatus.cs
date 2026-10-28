// Taj's Tokens | SystemTrayStatus.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public sealed record SystemTrayStatus(
    string Tooltip,
    int? ConstrainedRemainingPercent,
    bool IsFresh,
    string HealthLabel)
{
    public IReadOnlyList<string> SummaryRows { get; init; } = [];
    public bool Equals(SystemTrayStatus? other) => other is not null && Tooltip == other.Tooltip &&
        ConstrainedRemainingPercent == other.ConstrainedRemainingPercent && IsFresh == other.IsFresh &&
        HealthLabel == other.HealthLabel && SummaryRows.SequenceEqual(other.SummaryRows);
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(Tooltip); hash.Add(ConstrainedRemainingPercent); hash.Add(IsFresh); hash.Add(HealthLabel);
        foreach (string row in SummaryRows) hash.Add(row);
        return hash.ToHashCode();
    }
}
