// Taj's Tokens | CodexDailyPairing.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

public sealed record CodexDailyPair(
    string Date,
    decimal? NativeCredits,
    decimal? RelativePercent,
    decimal? CreditsPerPercentagePoint,
    IReadOnlyList<string> Exclusions);

public sealed record CodexDailyPairingReport(
    string Policy,
    string? CountsObservationId,
    string? RelativeObservationId,
    IReadOnlyList<string> Exclusions,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<CodexDailyPair> Days)
{
    public string? RelativeRangeStart { get; init; }
    public string? RelativeRangeEnd { get; init; }

    public string ToDisplayText()
    {
        CodexDailyPair[] hypotheses = Days.Where(x => x.CreditsPerPercentagePoint is not null).ToArray();
        var lines = new List<string>
        {
            $"Native daily pairing ({Policy}): {hypotheses.Length} ratio hypotheses / {Days.Count} observed dates. Not a validated workload scale or current quota conversion.",
        };
        lines.Add(
            $"Relative report requested range: {RelativeRangeStart ?? "unknown"} through {RelativeRangeEnd ?? "unknown"}. Values depend on this range; reported percent is not allowance consumption. Ratios from different ranges are not comparable.");
        if (Exclusions.Count > 0) lines.Add("Report exclusions: " + string.Join("; ", Exclusions));
        if (Assumptions.Count > 0) lines.Add("Hypothesis assumptions: " + string.Join("; ", Assumptions));
        foreach (IGrouping<string, string> reason in Days.SelectMany(x => x.Exclusions).GroupBy(x => x).OrderBy(x => x.Key))
            lines.Add($"  {reason.Key}: {reason.Count()} dates (reasons can overlap)");
        foreach (CodexDailyPair day in hypotheses)
            lines.Add(
                FormattableString.Invariant(
                    $"  {day.Date}: {day.NativeCredits} native reported credits / {day.RelativePercent} report-relative percent = {day.CreditsPerPercentagePoint:0.######} credits per report-relative point (range-local hypothesis only)"));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Pairs immutable daily snapshots, never price-derived credits or overlapping polls.</summary>
public static class CodexDailyPairing
{
    public const string Policy = "codex-daily-native-pairing/v2";
    public const decimal MinimumPercent = 0.01m;

    public static CodexDailyPairingReport Evaluate(IEnumerable<CodexServerObservation> observations)
    {
        // Latest attempt, including failure. Never search backward for a success to force a pair.
        CodexServerObservation[] snapshots = observations.ToArray();
        CodexServerObservation? counts = Latest(CodexServerSurface.DailyCounts);
        CodexServerObservation? relative = Latest(CodexServerSurface.DailyRelativeUsage);

        CodexServerObservation? Latest(CodexServerSurface surface)
        {
            return snapshots.Where(x => x.Surface == surface)
                .OrderByDescending(x => x.CollectedAtUtc).ThenByDescending(x => x.Id, StringComparer.Ordinal).FirstOrDefault();
        }

        var exclusions = new List<string>();
        var assumptions = new List<string>
        {
            "UTC daily buckets from the inspected endpoint contract",
            "count totals and summed relative surfaces describe the same disjoint product scope; seat scope is not independently reported",
            "historical denominator era is unknown; ratios cannot label a five-hour or weekly cycle",
            "relative normalization depends on requested range; its formula and stability within a fixed range remain unverified",
        };
        CodexDailyReport? a = counts?.DailyReport;
        CodexDailyReport? b = relative?.DailyReport;
        if (counts is null || relative is null) exclusions.Add("missing-report");
        if (!Available(counts) || !Available(relative)) exclusions.Add("latest-attempt-unavailable");
        if (counts is not null && relative is not null)
        {
            if (!Scoped(counts) || !Scoped(relative) || counts.CorrelatedAccountKey != relative.CorrelatedAccountKey)
                exclusions.Add("account-scope-not-compatible");
            if (counts.AccountBracket is null || counts.AccountBracket != relative.AccountBracket)
                exclusions.Add("not-same-bracketed-collection");
            if (counts.ContractVersion != relative.ContractVersion || counts.ClientVersion != relative.ClientVersion)
                exclusions.Add("source-version-mismatch");
        }
        if (a is not null && b is not null)
        {
            if (a.SourceContract != "codex-private-daily/v1" || b.SourceContract != a.SourceContract ||
                a.Endpoint != "wham/analytics/daily-workspace-usage-counts" || b.Endpoint != "wham/usage/daily-token-usage-breakdown")
                exclusions.Add("unsupported-native-contract");
            if (a.Units != "credit" || b.Units != "percent") exclusions.Add("incompatible-or-unknown-units");
            if (a.StartDate != b.StartDate || a.EndDate != b.EndDate) exclusions.Add("requested-range-mismatch");
            if (a.GroupBy is not (null or "day") || b.GroupBy is not (null or "day")) exclusions.Add("unsupported-grain");
            if (string.IsNullOrEmpty(a.Plan) || a.Plan != b.Plan) exclusions.Add("plan-context-not-compatible");
            if (string.IsNullOrEmpty(a.PolicyBefore) || a.PolicyBefore != a.PolicyAfter ||
                a.PolicyBefore != b.PolicyBefore || b.PolicyBefore != b.PolicyAfter)
                exclusions.Add("collection-policy-or-cycle-context-not-compatible");
            if (a.DataFreshness != b.DataFreshness) exclusions.Add("freshness-mismatch");
            else if (a.DataFreshness is null)
                assumptions.Add("no backend as-of supplied; same bracketed fetch is used as provisional revision compatibility");
            if (a.GroupBy is null || b.GroupBy is null)
                assumptions.Add("day grain follows the adapter request; response grain was not supplied");
            if (a.Days.GroupBy(x => x.Date).Any(x => x.Count() > 1) || b.Days.GroupBy(x => x.Date).Any(x => x.Count() > 1))
                exclusions.Add("duplicate-date-conflict");
        }
        var days = new List<CodexDailyPair>();
        if (a is not null && b is not null && !exclusions.Contains("duplicate-date-conflict"))
        {
            Dictionary<string, CodexDailyReportRow> countDays = a.Days.ToDictionary(x => x.Date);
            Dictionary<string, CodexDailyReportRow> relativeDays = b.Days.ToDictionary(x => x.Date);
            DateOnly completedBefore = DateOnly.FromDateTime(
                (counts!.FetchStartedAtUtc < relative!.FetchStartedAtUtc ? counts.FetchStartedAtUtc : relative.FetchStartedAtUtc)
                .UtcDateTime);
            foreach (string date in countDays.Keys.Union(relativeDays.Keys).Order(StringComparer.Ordinal))
            {
                var reasons = new List<string>();
                countDays.TryGetValue(date, out CodexDailyReportRow? count);
                relativeDays.TryGetValue(date, out CodexDailyReportRow? usage);
                if (count is null || usage is null) reasons.Add("date-not-in-both-reports");
                if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
                    reasons.Add("invalid-date");
                else if (day >= completedBefore) reasons.Add("incomplete-current-or-future-day");
                if (string.CompareOrdinal(date, a.StartDate) < 0 || string.CompareOrdinal(date, a.EndDate) >= 0 ||
                    string.CompareOrdinal(date, b.StartDate) < 0 || string.CompareOrdinal(date, b.EndDate) >= 0)
                    reasons.Add("outside-common-range-interior"); // End-date inclusion varies by route; do not infer it.
                if (count?.Credits is null) reasons.Add("missing-native-credits");
                else if (count.Credits == 0) reasons.Add("zero-native-credits-no-scale-evidence");
                else if (count.Credits < 0) reasons.Add("negative-native-credits");
                if (count?.OnDemandCredits is null) reasons.Add("credit-balance-spill-unknown");
                else if (count.OnDemandCredits != 0) reasons.Add("credit-balance-spill-present");
                decimal? percent = null;
                if (usage?.SurfaceUsage is not { Count: > 0 } surfaces)
                {
                    reasons.Add("missing-relative-surfaces");
                }
                else if (surfaces.Values.Any(x => x < 0))
                {
                    reasons.Add("negative-relative-usage");
                }
                else
                {
                    try
                    {
                        percent = surfaces.Values.Sum();
                    }
                    catch (OverflowException)
                    {
                        reasons.Add("relative-sum-overflow");
                    }
                    if (percent is <= MinimumPercent) reasons.Add("zero-or-tiny-relative-usage");
                }
                decimal? ratio = null;
                if (exclusions.Count == 0 && reasons.Count == 0)
                {
                    try
                    {
                        ratio = count!.Credits!.Value / percent!.Value;
                    }
                    catch (OverflowException)
                    {
                        reasons.Add("ratio-overflow");
                    }
                }
                days.Add(new CodexDailyPair(date, count?.Credits, percent, ratio, reasons));
            }
        }
        return new CodexDailyPairingReport(Policy, counts?.Id, relative?.Id, exclusions, assumptions, days)
        {
            RelativeRangeStart = b?.StartDate, RelativeRangeEnd = b?.EndDate,
        };
    }

    private static bool Available(CodexServerObservation? row)
    {
        return row is { DailyReport: not null, State: ServerEvidenceState.Available or ServerEvidenceState.Empty };
    }

    private static bool Scoped(CodexServerObservation row)
    {
        return row.AccountEvidence == AccountEvidenceClass.ServerCorrelated &&
               row.CorrelatedAccountKey is { Length: > 0 } account && row.AccountBracket is { } bracket &&
               bracket.BeforeAccountKey == account && bracket.AfterAccountKey == account &&
               bracket.BeforeCollectedAtUtc <= row.FetchStartedAtUtc && bracket.AfterCollectedAtUtc >= row.CollectedAtUtc;
    }
}