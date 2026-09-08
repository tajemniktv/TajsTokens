using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Account-local conditional scenarios, selected using matured chronological outcomes.</summary>
public sealed class ScenarioPlannerService
{
    private const int MinimumSamples = 12;

    public ScenarioEstimate Estimate(ScenarioRequest request, IReadOnlyList<ScenarioHistorySample> history,
        DateTimeOffset? evaluatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(history);
        if (!double.IsFinite(request.DurationHours) || request.DurationHours <= 0 || request.DurationHours > 168 ||
            request.RootAgents < 0 || request.Subagents < 0 || (long)request.RootAgents + request.Subagents <= 0 ||
            !double.IsFinite(request.IntensityMultiplier) || request.IntensityMultiplier <= 0 || request.IntensityMultiplier > 5)
            throw new ArgumentOutOfRangeException(nameof(request));
        var now = evaluatedAtUtc ?? DateTimeOffset.UtcNow;
        return new ScenarioEstimate(request, Build(QuotaWindowKind.FiveHour), Build(QuotaWindowKind.Weekly),
            "Account-local conditional estimates from authoritative meter intervals, including unchanged readings. " +
            "Active-session counts describe recent observed activity, not simultaneous compute or a causal agent-cost conversion. " +
            "A workload ridge candidate must beat the elapsed-time baseline on earlier chronological outcomes. " +
            "No universal token-to-quota conversion or calibrated exhaustion probability is assumed.");

        ScenarioWindowEstimate Build(QuotaWindowKind kind)
        {
            var samples = history.Where(x => x.Kind == kind && x.EndUtc <= now && x.EndUtc > x.StartUtc &&
                double.IsFinite(x.QuotaDeltaPercent) && x.QuotaDeltaPercent is >= 0 and <= 100 &&
                x.RootAgents >= 0 && x.Subagents >= 0 &&
                (x.ResetUtc is null || x.EndUtc <= x.ResetUtc)).OrderBy(x => x.StartUtc).ToArray();
            ScenarioWindowEstimate Unknown(string reason, int count) => new(kind, false, count, null, null, null, 0, reason);
            if (samples.Length > 0 && now - samples.Max(x => x.EndUtc) > TimeSpan.FromDays(30))
                return Unknown("History is stale; a usable interval within 30 days is required.", samples.Length);
            var source = samples.MaxBy(x => x.EndUtc)?.Source;
            samples = samples.Where(x => x.Source == source).ToArray();
            samples = samples.Where(x => x.EndUtc >= now.AddDays(-30) &&
                (string.IsNullOrWhiteSpace(request.Model) || string.Equals(x.DominantModel, request.Model, StringComparison.Ordinal)) &&
                (string.IsNullOrWhiteSpace(request.ReasoningEffort) || string.Equals(x.DominantReasoningEffort, request.ReasoningEffort, StringComparison.Ordinal))).ToArray();
            if (samples.Length < MinimumSamples)
                return Unknown($"Not enough matching history: need {MinimumSamples} intervals; have {samples.Length}. Requested model/effort cohorts are never replaced silently.", samples.Length);
            if (request.IntensityMultiplier != 1)
                return Unknown("Intensity multipliers have no observed source contract or validated response curve. Use intensity 1 for an evidence-backed conditional estimate.", samples.Length);
            if (request.DurationHours < samples.Min(Hours) || request.DurationHours > samples.Max(Hours) ||
                request.RootAgents < samples.Min(x => x.RootAgents) || request.RootAgents > samples.Max(x => x.RootAgents) ||
                request.Subagents < samples.Min(x => x.Subagents) || request.Subagents > samples.Max(x => x.Subagents))
                return Unknown("Requested duration or active-session counts are outside observed support; extrapolation is unavailable.", samples.Length);

            var trials = new List<(ScenarioHistorySample Sample, double BaselineError, double RidgeError, double SelectedError)>();
            foreach (var target in samples)
            {
                // No overlapping interval outcome may enter this prediction's training set.
                var prefix = samples.Where(x => x.EndUtc <= target.StartUtc && x.StartUtc < target.StartUtc).ToArray();
                if (prefix.Length < 6) continue;
                var baseline = Baseline(prefix, Hours(target));
                var fit = Fit(prefix);
                if (fit is null) continue;
                var ridge = Math.Clamp(fit.Predict(Features(target)), 0, 100);
                var previous = trials.Where(x => x.Sample.EndUtc <= target.StartUtc).ToArray();
                var useRidge = previous.Length >= 6 && previous.Average(x => x.RidgeError) + 0.1 < previous.Average(x => x.BaselineError) * 0.9;
                trials.Add((target, Math.Abs(target.QuotaDeltaPercent - baseline), Math.Abs(target.QuotaDeltaPercent - ridge),
                    Math.Abs(target.QuotaDeltaPercent - (useRidge ? ridge : baseline))));
            }
            var selectedRidge = trials.Count >= 6 && trials.Average(x => x.RidgeError) + 0.1 < trials.Average(x => x.BaselineError) * 0.9;
            var prediction = selectedRidge && Fit(samples) is { } model
                ? Math.Clamp(model.Predict([request.DurationHours, request.RootAgents, request.Subagents]), 0, 100)
                : Baseline(samples, request.DurationHours);
            // One comparable held-out error per reset generation, not thousands of correlated polls.
            var errors = trials.Where(x => x.Sample.ResetUtc is not null && x.Sample.ResetUtc < now &&
                    Hours(x.Sample) >= request.DurationHours / 2 && Hours(x.Sample) <= request.DurationHours * 2)
                .GroupBy(x => x.Sample.ResetUtc).Select(g => g.Last().SelectedError).ToArray();
            var radius = QuotaForecastBacktester.ErrorRadius(errors);
            return new ScenarioWindowEstimate(kind, true, samples.Length, prediction,
                radius is double r ? Math.Max(0, prediction - r) : null,
                radius is double r2 ? Math.Min(100, prediction + r2) : null, 0,
                $"Conditional {(selectedRidge ? "workload ridge" : "elapsed-time baseline")}; {trials.Count} chronological test outcomes. " +
                (radius is null ? "Uncertainty is learning: fewer than 8 comparable held-out reset generations. " : "Empirical 80%-target band from earlier held-out reset generations; coverage can change with workload. ") +
                "Unchanged meters are precision-limited, not exact zero burn. Session counts are not measured concurrency; no confidence probability is assigned.");
        }
    }

    private static double Hours(ScenarioHistorySample x) => (x.EndUtc - x.StartUtc).TotalHours;
    private static double[] Features(ScenarioHistorySample x) => [Hours(x), x.RootAgents, x.Subagents];
    private static AccountLocalRidge? Fit(ScenarioHistorySample[] samples) =>
        AccountLocalRidge.Fit(samples.Select(Features).ToArray(), samples.Select(x => x.QuotaDeltaPercent).ToArray(), 10);
    private static double Baseline(ScenarioHistorySample[] samples, double hours) =>
        Math.Clamp(samples.Sum(x => x.QuotaDeltaPercent) / samples.Sum(Hours) * hours, 0, 100);
}
