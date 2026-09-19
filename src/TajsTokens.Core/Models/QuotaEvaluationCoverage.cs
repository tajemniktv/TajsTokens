namespace TajsTokens.Core.Models;

/// <summary>Dataset-local diagnostics, not account completeness or billing-tier attribution.</summary>
public sealed record QuotaEvaluationCoverage(
    int TokenRecords, decimal ReportedTokens, decimal TokensWithModel, decimal TokensWithEffort,
    int CategoryMismatchRecords, int UnknownCollectionRecords, int CollectedAfterEventRecords,
    IReadOnlyDictionary<string, int> RequestedTierSettings, int MissingTierSettings,
    int UndatedTierSettings);

public sealed record QuotaCostCohortCoverage(QuotaHistoryCohort Cohort, double HorizonHours,
    int Intervals, int NativeAccountIntervals, int AssertedAccountIntervals,
    IReadOnlyDictionary<string, int> QualityCounts);
