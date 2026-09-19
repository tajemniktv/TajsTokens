using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public static class QuotaEvaluationCoverageBuilder
{
    public const string Boundary = "Dataset-local recorded evidence, not whole-account completeness. " +
        "Token coverage is amount-weighted; tier counts are physical requested-setting records, not token or billed-tier coverage. " +
        "Collection after event time is not by itself strict-origin ineligibility. Cohort flags overlap and describe built intervals, " +
        "not every rejected quota reading; do not sum horizons/sources as independent work. " +
        "Canonical copy/interleave coverage and provider reporting lag are not established by these counts.";

    public static QuotaEvaluationCoverage Build(CodexForecastDataset data)
    {
        decimal Total(Func<CodexPredictiveTokenEvent, bool> predicate) => data.Tokens.Where(predicate)
            .Sum(x => (decimal)Math.Max(0, x.ReportedTotalTokens));
        var settings = data.Workload.Where(x => x.EventType == "thread_settings_applied").ToArray();
        return new(data.Tokens.Count, Total(_ => true), Total(x => !string.IsNullOrWhiteSpace(x.Model)),
            Total(x => !string.IsNullOrWhiteSpace(x.ReasoningEffort)),
            data.Tokens.Count(x => new[] { x.UncachedInputTokens, x.CacheReadTokens, x.CacheWriteTokens,
                x.NonReasoningOutputTokens, x.ReasoningOutputTokens }.Sum(v => (decimal)v) != x.ReportedTotalTokens ||
                x.UncachedInputTokens < 0 || x.CacheReadTokens < 0 || x.CacheWriteTokens < 0 ||
                x.NonReasoningOutputTokens < 0 || x.ReasoningOutputTokens < 0 || x.ReportedTotalTokens < 0),
            data.Tokens.Count(x => x.CapturedAtUtc is null),
            data.Tokens.Count(x => x.CapturedAtUtc > x.ObservedAtUtc),
            settings.Where(x => !string.IsNullOrWhiteSpace(x.ServiceTier)).GroupBy(x => x.ServiceTier!, StringComparer.Ordinal)
                .OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal),
            settings.Count(x => string.IsNullOrWhiteSpace(x.ServiceTier)), settings.Count(x => x.ObservedAtUtc is null));
    }

    public static IReadOnlyList<QuotaCostCohortCoverage> Cohorts(IReadOnlyList<QuotaCostObservation> rows) =>
        rows.GroupBy(x => (x.Cohort, x.HorizonHours)).Select(g => new QuotaCostCohortCoverage(g.Key.Cohort,
            g.Key.HorizonHours, g.Count(), g.Count(x => x.Cohort.AccountKey is not null),
            g.Count(x => x.QualityFlags.Contains("user-asserted-account-association") ||
                x.QualityFlags.Contains("user-confirmed-rollout-ownership-native-account-id-absent")),
            g.SelectMany(x => x.QualityFlags.Distinct(StringComparer.Ordinal)).GroupBy(x => x, StringComparer.Ordinal)
                .OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal))).ToArray();
}
