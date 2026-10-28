using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public static class WorkloadReferenceProjection
{
    public static WorkloadReferenceResult Build(WorkloadReferenceState sources, CodexJointWorkload joint,
        IReadOnlyList<CodexLedgerEntry> ledger, DateTimeOffset atUtc)
        => Build(sources, joint, ledger, atUtc, true);

    public static WorkloadReferenceResult BuildConditional(WorkloadReferenceState sources, CodexJointWorkload joint, DateTimeOffset atUtc)
        => Build(sources, joint, [], atUtc, false);

    private static WorkloadReferenceResult Build(WorkloadReferenceState sources, CodexJointWorkload joint,
        IReadOnlyList<CodexLedgerEntry> ledger, DateTimeOffset atUtc, bool includeDateEffective)
    {
        var selection = sources.Selection;
        TtScoreAssessment? tt = selection is null ? null : TtJointScoring.Evaluate(selection.Basis, joint);
        if (sources.Prices?.LastGood is not { Origin: ApiPriceOrigin.OfficialCurated, ReferenceContext: { } context } primary)
            return new(sources, tt, null, null);
        var overrides = sources.Overrides?.LastGood;
        var fallback = sources.Fallback?.LastGood;
        if (overrides is not null && overrides.Origin != ApiPriceOrigin.LocalOverride ||
            fallback is not null && fallback.Origin is not (ApiPriceOrigin.CommunityFallback or ApiPriceOrigin.LegacyPinnedFallback))
            return new(sources with { Issue = "invalid-selected-price-role" }, tt, null, null);
        var cells = joint.Cells.Concat(joint.Overflow is { } overflow ? [overflow] : []).Select(ApiPriceCatalogValuation.FromJoint).ToArray();
        ApiPriceValuation Calculate(IReadOnlyList<ApiPriceWorkloadItem> items, ApiPriceTimeMode mode) =>
            ApiPriceCatalogValuation.Calculate(primary, items, context, mode, atUtc, TimeSpan.FromDays(WorkloadReferenceResult.StaleAfterDays),
                overrides, fallback, selection?.FallbackSourceKey is not null);
        // Ledger observed times are qualified by the dataset reader. This does not infer actual billing context.
        bool hasEffectiveDates = primary.Rates.Concat(overrides?.Rates ?? []).Concat(fallback?.Rates ?? []).Any(x => x.EffectiveFromUtc is not null);
        var dated = !hasEffectiveDates ? cells : ledger.Select(x => new ApiPriceWorkloadItem(x.Workload.Model, x.Workload.UncachedInputTokens,
            x.Workload.CacheReadTokens, x.Workload.CacheWriteTokens, x.Workload.NonReasoningOutputTokens,
            x.Workload.ReasoningOutputTokens, x.Workload.ReportedTotalTokens, x.ObservedAtUtc)).ToArray();
        return new(sources, tt, Calculate(cells, ApiPriceTimeMode.SnapshotReference),
            includeDateEffective ? Calculate(dated, ApiPriceTimeMode.DateEffective) : null);
    }
}
