namespace TajsTokens.Core.Models;

/// <summary>Installation-local, recorded-token prediction. Never subscription quota or a billing conversion.</summary>
public sealed record TokenWorkloadForecast(DateTimeOffset GeneratedAtUtc, DateTimeOffset? LatestTokenAtUtc,
    int TokenEvents, int Sessions, IReadOnlyList<TokenHorizonPrediction> Predictions, string Methodology);

public sealed record TokenHorizonPrediction(double HorizonHours, double ExpectedTokens, string Model,
    int TrainingSamples, int ValidationSamples, double? ValidationMeanAbsoluteError,
    double? LowerTokens, double? UpperTokens, string Explanation);

public sealed record TokenForecastTrial(DateTimeOffset OriginUtc, DateTimeOffset OutcomeUtc,
    double ObservedTokens, TokenHorizonPrediction Prediction);

public sealed record TokenForecastScore(double HorizonHours, string Model, int Origins,
    double? MeanAbsoluteError, double? RootMeanSquaredError, int IntervalOrigins, double? IntervalCoverage,
    int WorkloadOrigins = 0);
