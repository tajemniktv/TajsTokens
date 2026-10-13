// Taj's Tokens | CodexDailyTokenReport.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

/// <summary>UTC-day aggregates of retained local Codex increments; account ownership is not inferred.</summary>
public sealed record CodexDailyTokenReport(
    string Schema,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string Coverage,
    string AccountAttribution,
    IReadOnlyList<CodexDailyTokenRow> Days);

public sealed record CodexDailyTokenRow(
    DateOnly DayUtc,
    long Events,
    long UncachedInput,
    long CacheRead,
    long CacheWrite,
    long NonReasoningOutput,
    long ReasoningOutput,
    long ReportedTotal);
