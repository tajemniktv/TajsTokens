using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Research;

public static partial class ForecastThresholdStudy
{
    private sealed record OriginInputs(string Alias, IGrouping<QuotaHistoryCohort, QuotaHistoryDecision> Cohort,
        QuotaSnapshot[] Quota, IReadOnlyList<IReadOnlyList<QuotaSnapshot>> Epochs,
        Dictionary<DateTimeOffset, QuotaSnapshot[]> Prefixes);

    private static IEnumerable<OriginInputs> PrepareOrigins(CodexForecastDataset data)
    {
        QuotaSnapshot[] strict = QuotaHistoryPolicy.AvailableRows(data.Quota, ForecastReplayAvailability.CollectedByOrigin);
        var cohorts = QuotaHistoryPolicy.Streams(QuotaHistoryPolicy.Describe(strict, data.CapturedAtUtc))
            .Where(x => x.Key.AccountKey is not null && x.First().Observation.Authority == QuotaObservationAuthority.ProviderAuthoritative)
            .OrderBy(x => x.Key.Kind).ThenBy(x => x.Min(r => r.Observation.CapturedAtUtc));
        int index = 0;
        foreach (var cohort in cohorts)
        {
            QuotaSnapshot[] quota = QuotaHistoryPolicy.ReplayRows(cohort);
            var epochs = QuotaForecastCalibration.SplitEpochs(quota);
            var prefixes = new Dictionary<DateTimeOffset, QuotaSnapshot[]>();
            foreach (var epoch in epochs)
            {
                DateTimeOffset? previous = null;
                foreach (QuotaSnapshot q in epoch)
                {
                    if (q.CapturedAtUtc - epoch[0].CapturedAtUtc < TimeSpan.FromMinutes(15) || q.UsedPercent is not >= 0 or >= 100 ||
                        previous is { } last && q.CapturedAtUtc - last < TimeSpan.FromMinutes(30)) continue;
                    previous = q.CapturedAtUtc;
                    prefixes[q.CapturedAtUtc] = epoch.TakeWhile(x => x.CapturedAtUtc <= q.CapturedAtUtc).ToArray();
                }
            }
            yield return new($"cohort-{++index:00}", cohort, quota, epochs, prefixes);
        }
    }

    /// <summary>Fast current-policy eligibility replay; no fitting sweep, source acquisition or evidence mutation.</summary>
    public static ForecastEligibilityReport ExplainEligibility(CodexForecastDataset data, CancellationToken token = default)
    {
        var result = new List<ForecastOriginEligibility>();
        foreach (OriginInputs input in PrepareOrigins(data))
        {
            token.ThrowIfCancellationRequested();
            CodexForecastDataset local = data with { Quota = input.Quota };
            var index = new CodexForecastFeatureIndex(local);
            var history = CodexScenarioHistoryBuilder.Build(local, ForecastReplayAvailability.CollectedByOrigin);
            foreach (double hours in new[] { .5, 1d })
            {
                DateTimeOffset[] origins = input.Prefixes.Where(x => x.Key.AddHours(hours) <= x.Value[^1].ResetsAtUtc)
                    .Select(x => x.Key).Order().ToArray();
                if (origins.Length == 0) continue;
                DateTimeOffset split = origins[(int)Math.Floor(origins.Length * .8)];
                var labels = Labels(input.Epochs, origins, hours);
                foreach (string period in new[] { "development", "final" })
                {
                    DateTimeOffset[] selected = origins.Where(x => period == "final" ? x >= split : x < split).ToArray();
                    var stages = new Dictionary<string, (int Entered, int Passed, Dictionary<string, int> Reasons)>();
                    var overlaps = new Dictionary<string, int>(StringComparer.Ordinal);
                    var inputConditions = new Dictionary<string, int>(StringComparer.Ordinal);
                    var examples = new Dictionary<string, ForecastEligibilityExample>(StringComparer.Ordinal);
                    var supportExamples = new Dictionary<string, ForecastSupportExample>(StringComparer.Ordinal);
                    var training = new Dictionary<string, (int Entered, int Passed, Dictionary<string, int> Reasons)>();
                    int workloadRejected = 0;
                    void Count(string stage, IReadOnlyList<string> reasons)
                    {
                        if (!stages.TryGetValue(stage, out var counts)) counts = (0, 0, new(StringComparer.Ordinal));
                        foreach (string reason in reasons) counts.Reasons[reason] = counts.Reasons.GetValueOrDefault(reason) + 1;
                        stages[stage] = (counts.Entered + 1, counts.Passed + (reasons.Count == 0 ? 1 : 0), counts.Reasons);
                    }
                    foreach (DateTimeOffset origin in selected)
                    {
                        token.ThrowIfCancellationRequested();
                        CodexForecastFeatures features = index.Build(origin, ForecastReplayAvailability.CollectedByOrigin);
                        IReadOnlyList<string> reasons = ScenarioHistoryEligibility.OriginReasons(features);
                        foreach (string reason in reasons) overlaps[reason] = overlaps.GetValueOrDefault(reason) + 1;
                        if (reasons.Count > 0)
                        {
                            workloadRejected++;
                            var retained = data.Tokens.Where(x => x.ObservedAtUtc > origin.AddHours(-2) && x.ObservedAtUtc <= origin).ToArray();
                            var timely = retained.Where(x => x.CapturedAtUtc <= origin).ToArray();
                            var conditions = new List<string>();
                            if (retained.Length == 0) conditions.Add("rejected-no-retained-dated-token-events");
                            if (retained.Any(x => x.CapturedAtUtc > origin)) conditions.Add("rejected-later-collected-tokens-present");
                            if (retained.Any(x => x.CapturedAtUtc is null)) conditions.Add("rejected-unknown-collection-tokens-present");
                            if (timely.Any(x => string.IsNullOrWhiteSpace(x.Model))) conditions.Add("rejected-timely-missing-model-present");
                            if (timely.Any(x => string.IsNullOrWhiteSpace(x.ReasoningEffort))) conditions.Add("rejected-timely-missing-effort-present");
                            foreach (string condition in conditions) inputConditions[condition] = inputConditions.GetValueOrDefault(condition) + 1;
                            string key = string.Join("+", reasons.Concat(conditions));
                            if (!examples.ContainsKey(key) && examples.Count < 8)
                            {
                                examples.Add(key, new($"example-{examples.Count + 1:00}", reasons.Concat(conditions).ToArray(), retained.Length, timely.Length,
                                    retained.Count(x => x.CapturedAtUtc is null), retained.Count(x => x.CapturedAtUtc > origin),
                                    features.TokenActiveRootSessions, features.TokenActiveSubagentSessions, features.TokenActiveUnknownSessions,
                                    timely.Count(x => string.IsNullOrWhiteSpace(x.Model)), timely.Count(x => string.IsNullOrWhiteSpace(x.ReasoningEffort))));
                            }
                        }
                        bool outcome = labels.TryGetValue(origin, out var observed) && (period == "final" || observed.OutcomeUtc < split);
                        // Outcome availability is a parallel branch over all candidates, not an input gate.
                        Count("observed-outcome", outcome ? [] : ["no-matching-outcome"]);
                        Count("timely-token-input", features.ObservedTokenEvents == 0 ? ["no-timely-token-events"] : []);
                        if (features.ObservedTokenEvents == 0) continue;
                        string[] roles = reasons.Where(x => x != "no-timely-token-events").ToArray();
                        Count("required-session-role", roles);
                        if (roles.Length > 0) continue;
                        var request = new ScenarioRequest(hours, features.TokenActiveRootSessions, features.TokenActiveSubagentSessions,
                            AccountKey: input.Cohort.Key.AccountKey) { Cohorts = [input.Cohort.Key] };
                        ScenarioHistorySelection selection = ScenarioHistoryEligibility.Select(request, history, input.Cohort.Key.Kind, origin);
                        foreach (var stage in selection.Stages)
                        {
                            if (!training.TryGetValue(stage.Stage, out var counts)) counts = (0, 0, new(StringComparer.Ordinal));
                            foreach (var reason in stage.Reasons) counts.Reasons[reason.Key] = counts.Reasons.GetValueOrDefault(reason.Key) + reason.Value;
                            training[stage.Stage] = (counts.Entered + stage.Entered!.Value, counts.Passed + stage.Passed!.Value, counts.Reasons);
                        }
                        string[] trainingReasons = selection.IsStale ? ["stale-matching-history"] :
                            selection.Samples.Count < ForecastEvaluationOptions.Default.ScenarioSamples ? ["insufficient-matching-history"] : [];
                        Count("compatible-training", trainingReasons);
                        if (trainingReasons.Length > 0) continue;
                        IReadOnlyList<string> support = ScenarioHistoryEligibility.RequestSupportReasons(request, selection.Samples);
                        Count("requested-mixture-support", support);
                        string supportKey = string.Join("+", support);
                        if (support.Count > 0 && !supportExamples.ContainsKey(supportKey) && supportExamples.Count < 8)
                            supportExamples.Add(supportKey, new($"support-example-{supportExamples.Count + 1:00}", support,
                                selection.Samples.Count, hours, selection.Samples.Min(x => (x.EndUtc - x.StartUtc).TotalHours),
                                selection.Samples.Max(x => (x.EndUtc - x.StartUtc).TotalHours), request.RootAgents,
                                selection.Samples.Min(x => x.RootAgents), selection.Samples.Max(x => x.RootAgents), request.Subagents,
                                selection.Samples.Min(x => x.Subagents), selection.Samples.Max(x => x.Subagents)));
                        if (support.Count > 0) continue;
                        ScenarioEstimate estimate = QuotaPredictionService.Simulate(request, history, origin);
                        ScenarioWindowEstimate window = input.Cohort.Key.Kind == QuotaWindowKind.Weekly ? estimate.Weekly : estimate.FiveHour;
                        Count("point-answer", window.ExpectedQuotaDeltaPercent is null ? ["point-unavailable"] : []);
                        if (window.ExpectedQuotaDeltaPercent is null) continue;
                        Count("paired-outcome", outcome ? [] : ["no-matching-outcome"]);
                        Count("uncertainty-band", window.LowerQuotaDeltaPercent is null || window.UpperQuotaDeltaPercent is null ?
                            ["insufficient-interval-evidence"] : []);
                    }
                    string[] order = ["timely-token-input", "required-session-role", "compatible-training", "requested-mixture-support",
                        "point-answer", "paired-outcome", "uncertainty-band", "observed-outcome"];
                    result.Add(new(input.Alias, input.Cohort.Key.Kind.ToString(), FormattableString.Invariant($"{hours:g}h"), period, split,
                        selected.Length, workloadRejected, overlaps, inputConditions, order.Select(stage =>
                            stages.TryGetValue(stage, out var counts) ? new ForecastEligibilityStage(stage, "candidate-origins", counts.Entered, counts.Passed, counts.Reasons) :
                                new ForecastEligibilityStage(stage, "candidate-origins", 0, 0, new Dictionary<string, int>())).ToArray(),
                        training.Select(x => new ForecastEligibilityStage(x.Key, "history-interval-origin-pairs", x.Value.Entered,
                            x.Value.Passed, x.Value.Reasons)).ToArray(), examples.Values.ToArray(), supportExamples.Values.ToArray()));
                }
            }
        }
        QuotaSnapshot[] strict = QuotaHistoryPolicy.AvailableRows(data.Quota, ForecastReplayAvailability.CollectedByOrigin);
        var decisions = QuotaHistoryPolicy.Describe(strict, data.CapturedAtUtc);
        QuotaHistoryDecision[] eligible = decisions.Where(x => x.Eligible).ToArray();
        return new("forecast-eligibility/v1", Fingerprint(data), data.CapturedAtUtc,
            [new("discovery-encoding", "physical-sources", null, null, new Dictionary<string, int>()),
             new("decoding", "physical-sources", null, null, new Dictionary<string, int>()),
             new("quota-collection-availability", "quota-readings", data.Quota.Count, strict.Length,
                 new Dictionary<string, int> { ["unknown-collection-non-authoritative"] = data.Quota.Count - strict.Length }),
             new("quota-history-policy", "quota-readings", decisions.Count, eligible.Length,
                 decisions.Where(x => !x.Eligible).GroupBy(x => x.Reason).ToDictionary(x => x.Key, x => x.Count())),
             new("scenario-account-authority", "quota-readings", eligible.Length,
                 eligible.Count(x => x.Cohort.AccountKey is not null && x.Observation.Authority == QuotaObservationAuthority.ProviderAuthoritative),
                 new Dictionary<string, int> { ["unknown-account"] = eligible.Count(x => x.Cohort.AccountKey is null),
                     ["not-provider-authoritative"] = eligible.Count(x => x.Observation.Authority != QuotaObservationAuthority.ProviderAuthoritative) })], result,
            "Dataset-local current-policy session-scenario eligibility using the threshold study's exact origins, split and labels. " +
            "Acquisition denominators are unknown: retained forecast rows cannot reconstruct source discovery, decode failures or untimed rows excluded by the reader. " +
            "Origin reason counts overlap; no timely tokens also means no classified token sessions. Sequential stage denominators shrink only when assessed; " +
            "outcome availability is an independent branch and missing outcomes do not withhold a point. Examples show one numeric case per reason combination, not a representative sample. " +
            "Model/effort are unconstrained in these scenario requests, so missing labels do not block them; explicit requests use matching history only. " +
            "Training-selection counts are interval/origin pairs: one interval can be assessed at many origins, not independent training work. " +
            "Quota account/cohort compatibility never assigns local work to that account. Counts are not account-wide coverage or independent outcomes; do not add horizons or cohorts. " +
            "No thresholds, training targets, source data or production model selection were changed.");
    }
}
