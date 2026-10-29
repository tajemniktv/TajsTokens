namespace TajsTokens.Core.Models;

/// <summary>Construction audit only. A ceiling outcome is not an ordinary or latent-cost training target.</summary>
public sealed record QuotaCostCandidate(QuotaHistoryCohort Cohort, DateTimeOffset GenerationStartUtc,
    DateTimeOffset ResetUtc, DateTimeOffset OriginUtc, DateTimeOffset NominalTargetUtc, DateTimeOffset OutcomeUtc,
    DateTimeOffset OriginalReportedOriginUtc, DateTimeOffset OriginalReportedOutcomeUtc,
    double HorizonHours, double StartUsed, double EndUsed, long RecordedTokens, int TokenObservations,
    bool CompleteTokenCategories, DateTimeOffset? TokenCostAvailableUtc, bool ValidCollectionClocks);
