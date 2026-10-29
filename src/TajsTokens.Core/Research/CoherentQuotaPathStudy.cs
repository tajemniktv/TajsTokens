using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Research;

public sealed record CoherentPathFit(QuotaWindowKind Window, int? WindowMinutes, string? ReportedSlot, int TrainingRows, DateTimeOffset TrainingFromUtc,
    DateTimeOffset TrainingThroughUtc, DateTimeOffset? TrainingAvailableUtc, double MaximumTrainingEnvelopeLoss,
    IReadOnlyList<string> CompatibilityIssues);
public sealed record CoherentPathCase(string Cohort, DateTimeOffset OriginUtc, QuotaPathWindow FiveHour,
    QuotaPathWindow Weekly, string PathRevision, uint Seed, IReadOnlyList<CoherentPathFit> Fits,
    IReadOnlyList<CoherentQuotaPathReport> Horizons);
public sealed record CoherentQuotaPathStudyReport(string Version, string EvidenceIdentity, DateTimeOffset CutoffUtc,
    ForecastReplayAvailability Availability, int ReportedScopeClocks, int AlignedWindowPairs, int TrainingReadyPairs,
    int ExaminedPairs, bool SamplingLimited, IReadOnlyDictionary<string, int> WithheldOrigins,
    IReadOnlyList<CoherentPathCase> Cases, string Boundary);

public static class CoherentQuotaPathStudy
{
    public const string Version = "coherent-quota-path-study/v1";
    public const int MaximumOrigins = 32;
    private sealed record Pair(QuotaSnapshot Five, QuotaSnapshot Week, QuotaCostObservation[][] Training, string Scope);

    public static CoherentQuotaPathStudyReport Evaluate(CodexForecastDataset source, ForecastReplayAvailability availability,
        CancellationToken token = default, bool includeSteps = false)
    {
        if (!Enum.IsDefined(availability)) throw new ArgumentException("Unknown availability mode.");
        bool strict = availability == ForecastReplayAvailability.CollectedByOrigin;
        var quota = QuotaHistoryPolicy.AvailableRows(source.Quota, availability).Where(q => q.CapturedAtUtc <= source.CapturedAtUtc)
            .Select(q => strict && (q.CollectedAtUtc is null || q.CollectedAtUtc < (q.ReplayMeasuredAtUtc ?? q.CapturedAtUtc) ||
                q.CollectedAtUtc > (q.ReplayMeasuredAtUtc ?? q.CapturedAtUtc).AddMinutes(5)) ? q with { UsedPercent = null } : q).ToArray();
        var data = source with { Quota = quota };
        var decisions = QuotaHistoryPolicy.Describe(quota, data.CapturedAtUtc);
        var reported = decisions.Where(d => d.Eligible && d.Observation.Authority == QuotaObservationAuthority.ProviderAuthoritative)
            .Select(d => d.Observation).ToArray();
        var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
        string Scope(QuotaSnapshot q)
        {
            // Serialization is only a local dictionary key; raw identities never enter the report.
            string key = System.Text.Json.JsonSerializer.Serialize(new { q.Provider, q.Profile, q.Source, q.AccountKey,
                QuotaHistoryPolicy.Cohort(q).LimitId, q.PlanType });
            if (!scopes.TryGetValue(key, out var alias)) scopes[key] = alias = "cohort-" + (scopes.Count + 1);
            return alias;
        }
        var groups = reported.GroupBy(q => (Scope: Scope(q), q.CapturedAtUtc)).OrderBy(g => g.Key.CapturedAtUtc).ToArray();
        var rows = QuotaCostObservationBuilder.BuildDetailed(data, token, horizons: [.5]).Observations.ToLookup(r => r.Cohort);
        var ready = new List<Pair>(); var withheld = new Dictionary<string, int>(); int aligned = 0;
        void Reject(string reason) => withheld[reason] = withheld.GetValueOrDefault(reason) + 1;
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            var five = group.Where(q => q.Kind == QuotaWindowKind.FiveHour).ToArray();
            var week = group.Where(q => q.Kind == QuotaWindowKind.Weekly).ToArray();
            if (five.Length != 1 || week.Length != 1) { Reject("missing-or-ambiguous-paired-window"); continue; }
            aligned++; var anchors = new[] { five[0], week[0] }; var origin = group.Key.CapturedAtUtc;
            if (anchors.Any(q => string.IsNullOrWhiteSpace(q.AccountKey))) { Reject("unknown-account"); continue; }
            if (origin.AddHours(2) > data.CapturedAtUtc) { Reject("two-hour-case-outside-range"); continue; }
            if (anchors.Any(q => q.ResetsAtUtc <= origin)) { Reject("expired-window"); continue; }
            var training = anchors.Select(q => rows[QuotaHistoryPolicy.Cohort(q)].Where(r => r.EndUtc <= origin &&
                (!strict || r.TokenCostEvidenceAvailableAtUtc <= origin)).OrderBy(r => r.StartUtc).Take(20).ToArray()).ToArray();
            if (training.Any(t => t.Length < 20)) { Reject("insufficient-mature-paired-training"); continue; }
            if (training.Any(t => !QuotaAccountingModel.HasTrainingWork(t) || t.Any(r => !r.HasCompleteTokenCategories || !QuotaCostIdentification.Valid(r))))
            { Reject("incomplete-or-no-positive-training-work"); continue; }
            ready.Add(new(five[0], week[0], training, group.Key.Scope));
        }
        var selected = ready.Count <= MaximumOrigins ? ready.ToArray() : Enumerable.Range(0, MaximumOrigins)
            .Select(i => ready[(int)((long)i * (ready.Count - 1) / (MaximumOrigins - 1))]).ToArray();
        var index = new CodexForecastFeatureIndex(data); var cache = new WorkloadCompositionPrediction.PathCache();
        var fits = new Dictionary<string, Func<QuotaCostObservation, double>>();
        var cases = new List<CoherentPathCase>();
        foreach (var pair in selected)
        {
            token.ThrowIfCancellationRequested(); var origin = pair.Five.CapturedAtUtc;
            var paths = WorkloadCompositionPrediction.SamplePaths(index, origin, 2, 2, availability,
                WorkloadCompositionPrediction.DefaultPathSeed, cache, token);
            if (paths.Paths.Count == 0) { Reject("missing-or-incomplete-path-context"); continue; }
            var costs = new List<Func<CodexJointWorkload, double>>(); var fitInfo = new List<CoherentPathFit>();
            for (int i = 0; i < 2; i++)
            {
                var training = pair.Training[i]; var anchor = i == 0 ? pair.Five : pair.Week;
                string key = pair.Scope + ":" + i + ":" + anchor.WindowMinutes + ":" + string.Join(",", training.Select(t => t.StartUtc.UtcTicks));
                if (!fits.TryGetValue(key, out var fit)) fits[key] = fit = QuotaAccountingModel.FitFrozen(training, "joint-categories", token);
                double Cost(CodexJointWorkload work) => fit(ComposedQuotaValidation.Project(training[0],
                    ComposedQuotaValidation.DescribeJoint(work, paths.Context)));
                costs.Add(Cost);
                var check = new ComposedQuotaValidation.JointPathInputCheck(training, fit).Describe(paths.Mean!, 2, paths, Cost);
                fitInfo.Add(new(anchor.Kind, anchor.WindowMinutes, anchor.Lane is "primary" or "secondary" ? anchor.Lane : null,
                    training.Length, training[0].StartUtc, training.Max(t => t.EndUtc),
                    training.Any(t => t.TokenCostEvidenceAvailableAtUtc is null) ? null : training.Max(t => t.TokenCostEvidenceAvailableAtUtc),
                    check.MaximumTrainingEnvelopeViolation, check.InputCompatibilityReasons));
            }
            var fiveWindow = new QuotaPathWindow(QuotaWindowKind.FiveHour, pair.Five.RemainingPercent!.Value, pair.Five.ResetsAtUtc!.Value, []);
            var weekWindow = new QuotaPathWindow(QuotaWindowKind.Weekly, pair.Week.RemainingPercent!.Value, pair.Week.ResetsAtUtc!.Value, []);
            cases.Add(new(pair.Scope, origin, fiveWindow, weekWindow, paths.InputRevision, paths.Seed, fitInfo,
                ComposedQuotaValidation.EvaluationHorizons.Select(h => CoherentQuotaPaths.Evaluate(paths, fiveWindow, weekWindow,
                    costs[0], costs[1], origin.AddHours(h), token, includeSteps)).ToArray()));
        }
        return new(Version, ForecastThresholdStudy.Fingerprint(data), data.CapturedAtUtc, availability, groups.Length,
            aligned, ready.Count, selected.Length, ready.Count > MaximumOrigins, withheld, cases,
            "Explicit conditional mechanical study, not a first-exhaustion predictor or probability validation. " +
            "Only exactly aligned eligible provider-authoritative windows in the same known account/source/plan/bucket are paired. Primary/secondary are retained response slots, not semantic window identities. " +
            "The first 20 earlier matured 30-minute same-cohort cost intervals fit each frozen joint map; no future threshold labels select or tune it. " +
            "Fit constraints/novel or unknown coordinates remain explicit compatibility issues; numeric propagation under an imperfect map is a sensitivity case, not identified quota cost. " +
            "Up to 32 evenly spaced training-ready origins use one two-hour-context ensemble per origin, shared by both windows and all four horizon prefixes. Sampling and context refusals are explicit. " +
            "Native cases supply only reported current reset boundaries; no next capacity is inferred. Post-reset support and exact reset-order ties remain unresolved. " +
            "Reconstruction and collection-qualified input replay are separate; neither is an actually issued forecast. Local workload is not proven account attribution. " +
            "No threshold probability, delivered warning, execution-stop timing, public TT conversion or live selection follows.");
    }
}
