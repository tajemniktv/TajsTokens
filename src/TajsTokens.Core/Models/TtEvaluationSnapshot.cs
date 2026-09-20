// Taj's Tokens | TtEvaluationSnapshot.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

/// <summary>Original saved research output, not original per-task scoring or a live forecast.</summary>
public sealed record TtEvaluationSnapshot(
    string Id,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset DatasetCapturedAtUtc,
    string Provider,
    string Profile,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    TtEvaluation Report);

public sealed record TtEvaluationArchiveEntry(
    string Id,
    DateTimeOffset? RecordedAtUtc,
    TtEvaluationSnapshot? Snapshot,
    string? Problem);