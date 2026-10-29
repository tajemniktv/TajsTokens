using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Research;

/// <summary>Explicit scenario input, not an inferred provider refill or observed future outcome.</summary>
public sealed record QuotaPathReset(DateTimeOffset AtUtc, DateTimeOffset NextResetUtc,
    double RemainingPercent, DateTimeOffset DeclaredAtUtc);
public sealed record QuotaPathWindow(QuotaWindowKind Window, double RemainingPercent, DateTimeOffset ResetUtc,
    IReadOnlyList<QuotaPathReset> Resets);
public sealed record CoherentQuotaStep(int SourceBin, DateTimeOffset FromUtc, DateTimeOffset ToUtc,
    double FiveHourDelta, double WeeklyDelta);
public sealed record CoherentQuotaPathOutcome(int Path, string State, DateTimeOffset KnownThroughUtc,
    DateTimeOffset? FirstCrossingFromUtc, DateTimeOffset? FirstCrossingThroughUtc,
    IReadOnlyList<QuotaWindowKind> CrossingWindowCandidates, string? Reason, IReadOnlyList<CoherentQuotaStep> Steps);
public sealed record CoherentQuotaPathReport(string Version, DateTimeOffset OriginUtc, DateTimeOffset ThroughUtc,
    int Paths, int CrossingPaths, int NoCrossingPaths, int UnresolvedPaths, double CrossingPathShare,
    double NoCrossingPathShare, double UnresolvedPathShare, int ExplicitScenarioResets,
    double? CrossingConditionalBinEndMinutesP10, double? CrossingConditionalBinEndMinutesP50,
    double? CrossingConditionalBinEndMinutesP90, IReadOnlyList<CoherentQuotaPathOutcome> Outcomes, string Boundary);

/// <summary>Shared-path threshold arithmetic only. No fitting, probability calibration or live selection.</summary>
public static class CoherentQuotaPaths
{
    public const string Version = "coherent-quota-paths/v1";
    public static CoherentQuotaPathReport Evaluate(JointWorkloadPaths paths, QuotaPathWindow fiveHour,
        QuotaPathWindow weekly, Func<CodexJointWorkload, double> fiveHourCost, Func<CodexJointWorkload, double> weeklyCost,
        DateTimeOffset through, CancellationToken token = default, bool includeSteps = false)
    {
        var origin = paths.Context.ObservationToUtc;
        if (paths.Paths.Count is < 1 or > 64 || through <= origin || through > origin.AddHours(paths.Context.HorizonHours) ||
            fiveHour.Window != QuotaWindowKind.FiveHour || weekly.Window != QuotaWindowKind.Weekly)
            throw new ArgumentException("Provide one shared bounded ensemble, both named windows and a target inside its horizon.");
        var windows = new[] { fiveHour, weekly };
        foreach (var window in windows)
        {
            if (!ValidAmount(window.RemainingPercent) || window.ResetUtc <= origin || window.Resets.Count > 24)
                throw new ArgumentException("Invalid current budget/reset.");
            var next = window.ResetUtc;
            foreach (var reset in window.Resets)
            {
                if (reset.AtUtc != next || reset.NextResetUtc <= reset.AtUtc || reset.DeclaredAtUtc > origin || !ValidAmount(reset.RemainingPercent))
                    throw new ArgumentException("Reset assumptions must form an ordered schedule declared by origin, with explicit budgets and next boundaries.");
                next = reset.NextResetUtc;
            }
        }
        var supportedEnds = windows.Select(w => w.Resets.Count == 0 ? w.ResetUtc : w.Resets[^1].NextResetUtc).ToArray();
        var cap = supportedEnds.Min() < through ? supportedEnds.Min() : through;
        var outcomes = new List<CoherentQuotaPathOutcome>();
        for (int p = 0; p < paths.Paths.Count; p++)
        {
            token.ThrowIfCancellationRequested();
            var path = paths.Paths[p]; var expected = origin;
            foreach (var step in path.Steps)
            {
                if (expected >= through) break;
                if (step.FromUtc != expected || step.ToUtc <= step.FromUtc || !step.Workload.IsComplete)
                    throw new ArgumentException("Every path must have contiguous complete joint steps from the same origin.");
                expected = step.ToUtc;
            }
            if (expected < through) throw new ArgumentException("A path does not cover the requested target.");
            var remaining = windows.Select(w => w.RemainingPercent).ToArray();
            var resets = windows.Select(w => w.ResetUtc).ToArray(); var resetIndex = new int[2];
            var trace = new List<CoherentQuotaStep>();
            CoherentQuotaPathOutcome? result = null;
            var exhausted = Enumerable.Range(0, 2).Where(i => remaining[i] == 0).Select(i => windows[i].Window).ToArray();
            if (exhausted.Length > 0) result = new(p, "crossed", origin, origin, origin, exhausted, null, []);
            foreach (var step in result is null ? path.Steps : [])
            {
                token.ThrowIfCancellationRequested();
                if (step.FromUtc >= cap) break;
                // Both conversions see the exact same immutable joint step. Never draw another path per window.
                double[] costs = [fiveHourCost(step.Workload), weeklyCost(step.Workload)];
                if (costs.Any(c => !double.IsFinite(c) || c < 0))
                { result = new(p, "unresolved", step.FromUtc, null, null, [], "invalid-step-cost", []); break; }
                if (includeSteps) trace.Add(new(step.SourceBin, step.FromUtc, step.ToUtc, costs[0], costs[1]));
                var end = step.ToUtc < cap ? step.ToUtc : cap;
                var cuts = windows.SelectMany(w => w.Resets.Select(r => r.AtUtc).Append(w.ResetUtc))
                    .Where(t => t > step.FromUtc && t < end).Append(step.FromUtc).Append(end).Distinct().Order().ToArray();
                for (int s = 0; s < cuts.Length - 1; s++)
                {
                    var a = cuts[s]; var b = cuts[s + 1];
                    ApplyResets(a);
                    exhausted = Enumerable.Range(0, 2).Where(i => remaining[i] == 0).Select(i => windows[i].Window).ToArray();
                    if (exhausted.Length > 0) { result = new(p, "crossed", a, a, a, exhausted, null, []); break; }
                    double fraction = (b - a).Ticks / (double)(step.ToUtc - step.FromUtc).Ticks;
                    var crossed = new List<QuotaWindowKind>(); bool resetTie = false, definite = false;
                    for (int i = 0; i < 2; i++)
                    {
                        double cost = costs[i] * fraction;
                        if (cost >= remaining[i])
                        {
                            crossed.Add(windows[i].Window);
                            bool tie = (b == resets[i] || b == supportedEnds.Min()) && Math.Abs(cost - remaining[i]) <= 1e-12;
                            resetTie |= tie; definite |= !tie;
                        }
                        remaining[i] = Math.Max(0, remaining[i] - cost);
                    }
                    if (definite) { result = new(p, "crossed", b, a, b, crossed, null, []); break; }
                    if (resetTie) { result = new(p, "unresolved", b, null, null, [], "reset-order-tie", []); break; }
                }
                if (result is not null) break;
            }
            if (result is null)
            {
                ApplyResets(cap);
                exhausted = Enumerable.Range(0, 2).Where(i => remaining[i] == 0).Select(i => windows[i].Window).ToArray();
                result = exhausted.Length > 0 ? new(p, "crossed", cap, cap, cap, exhausted, null, []) :
                    supportedEnds.Min() <= through ? new(p, "unresolved", cap, null, null, [], "unsupported-reset", []) :
                    new(p, "no-crossing", through, null, null, [], null, []);
            }
            outcomes.Add(result with { Steps = includeSteps ? trace : [] });

            void ApplyResets(DateTimeOffset at)
            {
                for (int i = 0; i < 2; i++)
                    if (resetIndex[i] < windows[i].Resets.Count && windows[i].Resets[resetIndex[i]].AtUtc == at)
                    {
                        var reset = windows[i].Resets[resetIndex[i]++]; remaining[i] = reset.RemainingPercent; resets[i] = reset.NextResetUtc;
                    }
            }
        }
        var crossing = outcomes.Where(o => o.State == "crossed").Select(o => (o.FirstCrossingThroughUtc!.Value - origin).TotalMinutes).Order().ToArray();
        double? Q(double q) => crossing.Length == 0 ? null : crossing[Math.Clamp((int)Math.Ceiling(crossing.Length * q) - 1, 0, crossing.Length - 1)];
        int count = outcomes.Count, no = outcomes.Count(o => o.State == "no-crossing"), unresolved = outcomes.Count(o => o.State == "unresolved");
        return new(Version, origin, through, count, crossing.Length, no, unresolved, crossing.Length / (double)count, no / (double)count,
            unresolved / (double)count, windows.Sum(w => w.Resets.Count), Q(.1), Q(.5), Q(.9), outcomes,
            "Conditional finite-path arithmetic, not calibrated exhaustion probabilities. Each immutable joint step feeds both additive frozen cost maps. " +
            "Cost is allocated uniformly inside a step when a reset/target splits it; first crossing is reported as a bin, not an observed exact time. " +
            "First crossing is found per shared path before timing quantiles; marginal window quantiles are never combined. " +
            "No-crossing and unresolved paths retain their original ensemble mass. Timing summaries condition only on known crossing paths. " +
            "Unknown post-reset capacity ends joint support; explicit reset schedules are caller-declared scenarios known by origin, never inferred 100% refills. Exact old-budget exhaustion at reset is an unresolved ordering tie. " +
            "Allowance exhaustion is not execution stop, purchases, grace or a guarantee that future work happens. No live selection or provider probability claim.");
    }

    private static bool ValidAmount(double amount) => double.IsFinite(amount) && amount is >= 0 and <= 100;
}
