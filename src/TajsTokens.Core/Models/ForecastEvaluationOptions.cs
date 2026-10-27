namespace TajsTokens.Core.Models;

/// <summary>Explicit experimental inputs. Released callers always use Default; no mutable global policy.</summary>
public sealed record ForecastEvaluationOptions(
    double BlockHours = 6,
    int CalibrationGroups = 8,
    int SelectionSamples = 6,
    int IntervalSamples = 20,
    int TrainingSamples = 12,
    int ScenarioSamples = 12,
    int ComposedOutcomes = 16,
    double ImprovementMultiplier = 1)
{
    public static ForecastEvaluationOptions Default { get; } = new();

    public ForecastEvaluationOptions Validate()
    {
        if (!double.IsFinite(BlockHours) || BlockHours <= 0 || CalibrationGroups < 1 || SelectionSamples < 1 ||
            IntervalSamples < 1 || TrainingSamples < 1 || ScenarioSamples < 1 || ComposedOutcomes < 1 ||
            !double.IsFinite(ImprovementMultiplier) || ImprovementMultiplier <= 0 || ImprovementMultiplier >= 10)
            throw new ArgumentOutOfRangeException(nameof(ForecastEvaluationOptions));
        return this;
    }
}
