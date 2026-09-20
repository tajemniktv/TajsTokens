// Taj's Tokens | QuotaEvaluationCoverageBuilder.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

public static class QuotaEvaluationCoverageBuilder
{
    public const string Boundary = "Dataset-local recorded evidence, not whole-account completeness. " +
                                   "Token coverage is amount-weighted; tier counts are physical requested-setting records, not token or billed-tier coverage. " +
                                   "Collection after event time is not by itself strict-origin ineligibility. Cohort flags overlap and describe built intervals, " +
                                   "not every rejected quota reading; do not sum horizons/sources as independent work. " +
                                   "Canonical copy/interleave coverage and provider reporting lag are not established by these counts.";

    public static bool HasCompleteCategories(CodexPredictiveTokenEvent x)
    {
        long[] parts = [x.UncachedInputTokens, x.CacheReadTokens, x.CacheWriteTokens, x.NonReasoningOutputTokens, x.ReasoningOutputTokens];
        return x.ReportedTotalTokens >= 0 && parts.All(v => v >= 0) && parts.Sum(v => (decimal)v) == x.ReportedTotalTokens;
    }

    public static QuotaEvaluationCoverage Build(CodexForecastDataset data)
    {
        decimal Total(Func<CodexPredictiveTokenEvent, bool> predicate)
        {
            return data.Tokens.Where(predicate)
                .Sum(x => (decimal)Math.Max(0, x.ReportedTotalTokens));
        }

        CodexWorkloadObservation[] settings = data.Workload.Where(x => x.EventType == "thread_settings_applied").ToArray();
        return new QuotaEvaluationCoverage(
            data.Tokens.Count,
            Total(_ => true),
            Total(x => !string.IsNullOrWhiteSpace(x.Model)),
            Total(x => !string.IsNullOrWhiteSpace(x.ReasoningEffort)),
            data.Tokens.Count(x => !HasCompleteCategories(x)),
            data.Tokens.Count(x => x.CapturedAtUtc is null),
            data.Tokens.Count(x => x.CapturedAtUtc > x.ObservedAtUtc),
            settings.Where(x => !string.IsNullOrWhiteSpace(x.ServiceTier)).GroupBy(x => x.ServiceTier!, StringComparer.Ordinal)
                .OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal),
            settings.Count(x => string.IsNullOrWhiteSpace(x.ServiceTier)),
            settings.Count(x => x.ObservedAtUtc is null));
    }

    public static IReadOnlyList<QuotaCostCohortCoverage> Cohorts(IReadOnlyList<QuotaCostObservation> rows)
    {
        return rows.GroupBy(x => (x.Cohort, x.HorizonHours)).Select(g => new QuotaCostCohortCoverage(
            g.Key.Cohort,
            g.Key.HorizonHours,
            g.Count(),
            g.Count(x => x.Cohort.AccountKey is not null),
            g.Count(x => x.QualityFlags.Contains("user-asserted-account-association") ||
                         x.QualityFlags.Contains("user-confirmed-rollout-ownership-native-account-id-absent")),
            g.SelectMany(x => x.QualityFlags.Distinct(StringComparer.Ordinal)).GroupBy(x => x, StringComparer.Ordinal)
                .OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal))).ToArray();
    }
}