using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>
/// Fits a small account-local ridge model to observed quota-drop intervals. It deliberately learns
/// only from the user's own quota movement and concurrency history; it contains no universal
/// token-to-subscription-quota conversion.
/// </summary>
public sealed class ScenarioPlannerService
{
    private const int MinimumSamples = 6;
    private const int MaximumSampleAgeDays = 30;
    private const double Ridge = 0.15;

    public ScenarioEstimate Estimate(
        ScenarioRequest request,
        IReadOnlyList<ScenarioHistorySample> history,
        DateTimeOffset? evaluatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(history);

        if (!double.IsFinite(request.DurationHours) || request.DurationHours <= 0 || request.DurationHours > 168)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Scenario duration must be between 0 and 168 hours.");
        }

        if (request.RootAgents < 0 || request.Subagents < 0 || request.RootAgents + request.Subagents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "At least one root agent or subagent is required.");
        }

        if (!double.IsFinite(request.IntensityMultiplier) || request.IntensityMultiplier <= 0 || request.IntensityMultiplier > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Intensity multiplier must be greater than 0 and at most 5.");
        }

        var evaluationTime = (evaluatedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var fiveHour = BuildEstimate(QuotaWindowKind.FiveHour, request, history, evaluationTime);
        var weekly = BuildEstimate(QuotaWindowKind.Weekly, request, history, evaluationTime);
        return new ScenarioEstimate(
            request,
            fiveHour,
            weekly,
            "Account-local ridge regression over observed quota-drop intervals. Features are elapsed hours, root-agent-hours and subagent-hours; requested intensity scales those workload features. Prediction ranges come from historical residual error. Estimates require a usable interval within the last 30 days. No universal token→quota conversion is assumed.");
    }

    private static ScenarioWindowEstimate BuildEstimate(
        QuotaWindowKind kind,
        ScenarioRequest request,
        IReadOnlyList<ScenarioHistorySample> history,
        DateTimeOffset evaluationTime)
    {
        var baseSamples = history
            .Where(sample => sample.Kind == kind &&
                             sample.QuotaDeltaPercent > 0 &&
                             sample.EndUtc > sample.StartUtc &&
                             sample.RootAgents + sample.Subagents > 0 &&
                             sample.EndUtc <= evaluationTime)
            .ToArray();

        if (baseSamples.Length > 0)
        {
            var newestSample = baseSamples.Max(sample => sample.EndUtc);
            if (evaluationTime - newestSample > TimeSpan.FromDays(MaximumSampleAgeDays))
            {
                return new ScenarioWindowEstimate(
                    kind,
                    false,
                    baseSamples.Length,
                    null,
                    null,
                    null,
                    0.05,
                    $"{kind} history is stale. The newest usable interval ended {newestSample:yyyy-MM-dd}; a sample within the last {MaximumSampleAgeDays} days is required.");
            }
        }

        var cohort = baseSamples;
        var cohortLabel = "all observed workloads";
        var cohortPenalty = 1d;
        var fallbackNotes = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            var matching = cohort
                .Where(sample => string.Equals(sample.DominantModel, request.Model, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matching.Length >= MinimumSamples)
            {
                cohort = matching;
                cohortLabel = $"model {request.Model}";
            }
            else
            {
                cohortPenalty *= 0.85;
                fallbackNotes.Add($"Model {request.Model} did not have enough matching history, so the broader account cohort was used.");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
        {
            var matching = cohort
                .Where(sample => string.Equals(sample.DominantReasoningEffort, request.ReasoningEffort, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matching.Length >= MinimumSamples)
            {
                cohort = matching;
                cohortLabel += $", reasoning {request.ReasoningEffort}";
            }
            else
            {
                cohortPenalty *= 0.85;
                fallbackNotes.Add(
                    cohortLabel == "all observed workloads"
                        ? $"Reasoning {request.ReasoningEffort} did not have enough matching history, so the broader account cohort was used."
                        : $"Reasoning {request.ReasoningEffort} did not have enough matching history, so the {cohortLabel} cohort was retained.");
            }
        }

        if (cohort.Length < MinimumSamples)
        {
            return new ScenarioWindowEstimate(
                kind,
                false,
                cohort.Length,
                null,
                null,
                null,
                Math.Clamp(cohort.Length / (double)MinimumSamples * 0.25, 0, 0.25),
                $"Not enough {kind} quota-drop intervals yet. Need at least {MinimumSamples}; have {cohort.Length} usable sample(s).");
        }

        var x = cohort.Select(Features).ToArray();
        var y = cohort.Select(sample => sample.QuotaDeltaPercent).ToArray();
        var coefficients = FitRidge(x, y);
        var requestedFeatures = new[]
        {
            1d,
            request.DurationHours * request.IntensityMultiplier,
            request.DurationHours * request.RootAgents * request.IntensityMultiplier,
            request.DurationHours * request.Subagents * request.IntensityMultiplier
        };
        var rawPrediction = Dot(coefficients, requestedFeatures);
        var expected = Math.Clamp(rawPrediction, 0, 100);

        var residuals = cohort
            .Select((sample, index) => y[index] - Dot(coefficients, x[index]))
            .ToArray();
        var residualVariance = residuals.Sum(value => value * value) / Math.Max(1, residuals.Length - coefficients.Length);
        var residualSigma = Math.Sqrt(Math.Max(0, residualVariance));
        var minimumUncertainty = Math.Max(0.5, expected * 0.1);
        var uncertainty = Math.Max(minimumUncertainty, residualSigma * 1.28); // approximate central 80% empirical band
        var lower = Math.Clamp(expected - uncertainty, 0, 100);
        var upper = Math.Clamp(expected + uncertainty, 0, 100);

        var mean = Math.Max(0.5, y.Average());
        var fitFactor = 1d / (1d + residualSigma / mean);
        var sampleFactor = Math.Clamp(cohort.Length / 24d, 0.25, 1);
        var confidence = Math.Clamp(sampleFactor * fitFactor * cohortPenalty, 0.1, 0.92);

        var fallbackNote = fallbackNotes.Count > 0
            ? $" {string.Join(" ", fallbackNotes)} Confidence was reduced."
            : string.Empty;

        return new ScenarioWindowEstimate(
            kind,
            true,
            cohort.Length,
            expected,
            lower,
            upper,
            confidence,
            $"Estimated from {cohort.Length} {cohortLabel} interval(s). Historical residual σ ≈ {residualSigma:0.00} quota points.{fallbackNote}");
    }

    private static double[] Features(ScenarioHistorySample sample)
    {
        var hours = Math.Clamp((sample.EndUtc - sample.StartUtc).TotalHours, 1d / 3600d, 168d);
        return [1d, hours, hours * sample.RootAgents, hours * sample.Subagents];
    }

    private static double[] FitRidge(IReadOnlyList<double[]> x, IReadOnlyList<double> y)
    {
        const int columns = 4;
        var matrix = new double[columns, columns];
        var vector = new double[columns];

        for (var row = 0; row < x.Count; row++)
        {
            for (var i = 0; i < columns; i++)
            {
                vector[i] += x[row][i] * y[row];
                for (var j = 0; j < columns; j++)
                {
                    matrix[i, j] += x[row][i] * x[row][j];
                }
            }
        }

        for (var i = 1; i < columns; i++)
        {
            matrix[i, i] += Ridge;
        }

        return Solve(matrix, vector);
    }

    private static double[] Solve(double[,] matrix, double[] vector)
    {
        var size = vector.Length;
        var augmented = new double[size, size + 1];
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                augmented[row, column] = matrix[row, column];
            }
            augmented[row, size] = vector[row];
        }

        for (var pivot = 0; pivot < size; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < size; row++)
            {
                if (Math.Abs(augmented[row, pivot]) > Math.Abs(augmented[best, pivot]))
                {
                    best = row;
                }
            }

            if (best != pivot)
            {
                for (var column = pivot; column <= size; column++)
                {
                    (augmented[pivot, column], augmented[best, column]) =
                        (augmented[best, column], augmented[pivot, column]);
                }
            }

            var divisor = augmented[pivot, pivot];
            if (Math.Abs(divisor) < 1e-9)
            {
                divisor = divisor < 0 ? -1e-9 : 1e-9;
            }

            for (var column = pivot; column <= size; column++)
            {
                augmented[pivot, column] /= divisor;
            }

            for (var row = 0; row < size; row++)
            {
                if (row == pivot)
                {
                    continue;
                }

                var factor = augmented[row, pivot];
                for (var column = pivot; column <= size; column++)
                {
                    augmented[row, column] -= factor * augmented[pivot, column];
                }
            }
        }

        var result = new double[size];
        for (var row = 0; row < size; row++)
        {
            result[row] = augmented[row, size];
        }
        return result;
    }

    private static double Dot(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var result = 0d;
        for (var index = 0; index < Math.Min(left.Count, right.Count); index++)
        {
            result += left[index] * right[index];
        }
        return result;
    }
}
