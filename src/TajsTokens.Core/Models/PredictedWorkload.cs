namespace TajsTokens.Core.Models;

/// <summary>Origin-only composition projection, not actual future work or account-wide usage.</summary>
public sealed record PredictedWorkload(DateTimeOffset OriginUtc, double HorizonHours,
    double ExpectedTokens, IReadOnlyList<double> TokenCategories,
    IReadOnlyDictionary<string, double> ModelShares, IReadOnlyDictionary<string, double> EffortShares,
    int CompositionObservations, DateTimeOffset LatestCompositionAtUtc,
    ForecastReplayAvailability Availability, string Methodology);
