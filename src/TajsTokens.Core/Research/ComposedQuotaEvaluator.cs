using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Research;

public static class ComposedQuotaEvaluator
{
    public const string Version = ComposedQuotaValidation.Version;
    public static readonly double[] EvaluationHorizons = [5d / 60, .25, .5, 2];
    public static ComposedQuotaEvaluation Evaluate(CodexForecastDataset data, CancellationToken cancellationToken = default,
        ForecastReplayAvailability availability = ForecastReplayAvailability.ReconstructedEventTime,
        IReadOnlyList<double>? horizons = null) =>
        ComposedQuotaValidation.Evaluate(data, cancellationToken, availability, horizons ?? EvaluationHorizons);
}
