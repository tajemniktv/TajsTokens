// Taj's Tokens | CodexRolloutInspection.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public enum CodexRolloutComparisonKind
{
    Unresolved,
    IdenticalBytes,
    IdenticalOwnedRecords,
    PrefixOverlap,
    DifferentRecords,
}

/// <summary>Inspection-only comparison. Equality is not permission to merge or import evidence.</summary>
public sealed record CodexRolloutComparison(
    string AlternatePath,
    string? IndexedPath,
    CodexRolloutComparisonKind Kind,
    bool OwnershipEstablished,
    bool HasNonOwningPrefix,
    int? CommonOwnedRecords,
    string Detail);

public sealed record CodexRolloutInspectionReport(
    DateTimeOffset ObservedAtUtc,
    string? StateDatabasePath,
    int? UnindexedPaths,
    int Offset,
    IReadOnlyList<CodexRolloutComparison> Comparisons,
    string Diagnostic)
{
    public bool HasMore => UnindexedPaths is int count && Offset + Comparisons.Count < count;
}