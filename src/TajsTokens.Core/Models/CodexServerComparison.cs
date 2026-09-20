// Taj's Tokens | CodexServerComparison.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public sealed record CodexServerComparisonRow(
    string Scope,
    long? ServerTokens,
    long? LocalTokens,
    long? EstimatedCreditsMicros,
    AccountEvidenceClass Association,
    string Detail)
{
    public double? LocalToServerRatio => ServerTokens is > 0 && LocalTokens is not null ? (double)LocalTokens / ServerTokens : null;
}

public sealed record CodexServerComparisonReport(
    DateTimeOffset ReadAtUtc,
    IReadOnlyList<CodexServerObservation> LatestObservations,
    IReadOnlyList<CodexServerComparisonRow> Comparisons,
    int UserDeclaredAssociations,
    string Limitations)
{
    public string ToDisplayText()
    {
        return string.Join(
                   "\n",
                   LatestObservations.GroupBy(x => x.Surface).Select(g =>
                       $"{g.Key}: {string.Join(", ", g.GroupBy(x => x.State).Select(s => $"{s.Count()} {s.Key}"))} · latest fetch {g.Max(x => x.CollectedAtUtc).ToLocalTime():g}")) +
               "\n" + string.Join(
                   "\n",
                   LatestObservations.Where(x => x.Activity is not null).Select(x =>
                       $"Account report · CLI {x.ClientVersion ?? "unknown"} · {x.Activity!.DailyUsageBuckets?.Count.ToString() ?? "unavailable"} daily buckets · " +
                       $"peak daily {x.Activity.PeakDailyTokens?.ToString("N0") ?? "unknown"} · longest turn {x.Activity.LongestRunningTurnSec?.ToString() ?? "unknown"}s · " +
                       $"streak current/longest {x.Activity.CurrentStreakDays?.ToString() ?? "unknown"}/{x.Activity.LongestStreakDays?.ToString() ?? "unknown"} days. No backend as-of/completeness field.")) +
               $"\nUser-declared retained-source associations: {UserDeclaredAssociations}\n\n" +
               string.Join(
                   "\n",
                   Comparisons.Take(100).Select(x =>
                       $"{x.Scope}: backend {x.ServerTokens?.ToString("N0") ?? "unavailable"} tokens; local {x.LocalTokens?.ToString("N0") ?? "unavailable"}" +
                       (x.LocalToServerRatio is { } ratio ? $"; comparison ratio {ratio:0.000}" : "") +
                       (x.EstimatedCreditsMicros is { } credits ? $"; estimated credits {credits / 1_000_000m:0.######}" : "") +
                       $" · {x.Association}\n  {x.Detail}")) + "\n\n" + Limitations;
    }
}