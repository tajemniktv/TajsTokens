using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Research;

public sealed record ThresholdConfiguration(string Id, ForecastEvaluationOptions Options);
public sealed record ThresholdStudyScore(string Cohort, QuotaWindowKind Window, string Target, string Family,
    string Configuration, string Period, int CandidateOrigins, int PointOrigins, int BandOrigins,
    int PairedOutcomes, int NonOverlappingOutcomes, int EvaluationBlocks, int BandedOutcomes, int UnmatchedPredictions, double? MeanAbsoluteError, double? RootMeanSquaredError,
    double? PairedPersistenceMae, double? PairedPaceMae, double? IntervalCoverage, double? MeanIntervalWidth,
    int DistinctDays, int ResetCycles, IReadOnlyDictionary<string, int> Selections, IReadOnlyDictionary<string, int> UnsupportedReasons);
public sealed record ThresholdStudySplit(string Cohort, string Family, string Target, DateTimeOffset? FinalPeriodFromUtc,
    string DevelopmentChoice, string Status);
public sealed record ForecastThresholdReport(string Version, string DatasetIdentity, DateTimeOffset CapturedAtUtc,
    IReadOnlyList<ThresholdConfiguration> Configurations, IReadOnlyList<ThresholdStudySplit> Splits,
    IReadOnlyList<ThresholdStudyScore> Scores, string Methodology);

/// <summary>Explicit local research. Options never change released defaults or durable telemetry.</summary>
public static partial class ForecastThresholdStudy
{
    public const string Version = "forecast-threshold-study/v3-eligible-pace";
    private sealed record Point(DateTimeOffset Origin, DateTimeOffset Reset, double? Prediction, double? Actual,
        DateTimeOffset? Outcome, double? Lower, double? Upper, double? Persistence, double? Pace,
        string Model, string Reason);

    public static IReadOnlyList<ThresholdConfiguration> Configurations { get; } = BuildConfigurations();

    private static IReadOnlyList<ThresholdConfiguration> BuildConfigurations()
    {
        ForecastEvaluationOptions d = ForecastEvaluationOptions.Default;
        var result = new List<ThresholdConfiguration> { new("current", d) };
        foreach (int hours in new[] { 3, 12 }) result.Add(new($"block-{hours}h", d with { BlockHours = hours }));
        foreach (int count in new[] { 4, 12 }) result.Add(new($"groups-{count}", d with { CalibrationGroups = count }));
        foreach (double scale in new[] { .5, 2d })
        {
            string suffix = scale == .5 ? "half" : "double";
            result.Add(new("selection-" + suffix, d with { SelectionSamples = (int)(d.SelectionSamples * scale) }));
            result.Add(new("intervals-" + suffix, d with { IntervalSamples = (int)(d.IntervalSamples * scale) }));
            result.Add(new("training-" + suffix, d with { TrainingSamples = (int)(d.TrainingSamples * scale) }));
            result.Add(new("scenario-" + suffix, d with { ScenarioSamples = (int)(d.ScenarioSamples * scale) }));
            result.Add(new("composed-" + suffix, d with { ComposedOutcomes = (int)(d.ComposedOutcomes * scale) }));
            result.Add(new("margin-" + suffix, d with { ImprovementMultiplier = scale }));
        }
        return result.AsReadOnly();
    }

    public static ForecastThresholdReport Evaluate(CodexForecastDataset data, CancellationToken token = default,
        Action<string>? progress = null)
    {
        var scores = new List<ThresholdStudyScore>();
        var splits = new List<ThresholdStudySplit>();
        foreach (OriginInputs input in PrepareOrigins(data))
        {
            token.ThrowIfCancellationRequested();
            var cohort = input.Cohort;
            string id = input.Alias;
            progress?.Invoke($"{id}: {cohort.Key.Kind}, collection-time replay");
            QuotaSnapshot[] quota = input.Quota;
            CodexForecastDataset local = data with { Quota = quota };
            IReadOnlyList<IReadOnlyList<QuotaSnapshot>> epochs = input.Epochs;
            Dictionary<DateTimeOffset, QuotaSnapshot[]> prefixes = input.Prefixes;
            foreach (double? horizon in cohort.Key.Kind == QuotaWindowKind.Weekly ? new double?[] { .5, 2, 24, null } : [.5, 2, null])
            {
                token.ThrowIfCancellationRequested();
                string target = horizon is { } h ? FormattableString.Invariant($"{h:g}h") : "near-reset-proxy";
                DateTimeOffset[] origins = prefixes.Where(x => horizon is null || x.Key.AddHours(horizon.Value) <= x.Value[^1].ResetsAtUtc)
                    .Select(x => x.Key).Order().ToArray();
                progress?.Invoke($"{id}: outlook {target}, {origins.Length} candidate origins");
                var trials = QuotaPaceModels.Candidates.ToDictionary(model => model,
                    model => QuotaForecastCalibration.Replay(quota, model, horizon, token));
                var labels = Labels(epochs, origins, horizon);
                var points = new Dictionary<string, IReadOnlyList<Point>>();
                var cached = new Dictionary<ForecastEvaluationOptions, IReadOnlyList<Point>>();
                foreach (ThresholdConfiguration configuration in Configurations)
                {
                    ForecastEvaluationOptions effective = horizon is not null
                        ? configuration.Options with { BlockHours = 6, CalibrationGroups = 8, ScenarioSamples = 12, ComposedOutcomes = 16 }
                        : ForecastEvaluationOptions.Default with { CalibrationGroups = configuration.Options.CalibrationGroups,
                            ImprovementMultiplier = configuration.Options.ImprovementMultiplier };
                    if (cached.TryGetValue(effective, out IReadOnlyList<Point>? existing))
                    { points[configuration.Id] = existing; continue; }
                    token.ThrowIfCancellationRequested();
                    if (horizon is { } hours)
                    {
                        IReadOnlyDictionary<DateTimeOffset, QuotaHorizonPrediction> predictions = QuotaPredictionService.ReplayOriginsWithTrials(
                            local, hours, ForecastReplayAvailability.CollectedByOrigin, origins, token, configuration.Options, trials);
                        points[configuration.Id] = origins.Select(origin =>
                        {
                            QuotaHorizonPrediction? prediction = predictions.GetValueOrDefault(origin);
                            return Make(origin, horizon, prediction?.RemainingPercent, prediction?.LowerRemainingPercent,
                                prediction?.UpperRemainingPercent, prediction?.Model ?? "unavailable",
                                prediction is null ? "point-unavailable" : "", labels, prefixes);
                        }).ToArray();
                    }
                    else
                    {
                        IReadOnlyDictionary<DateTimeOffset, Forecast> predictions = QuotaPredictionService.ReplayResetOriginsWithTrials(
                            quota, origins, token, configuration.Options, trials);
                        points[configuration.Id] = origins.Select(origin =>
                        {
                            Forecast f = predictions[origin];
                            bool supported = f.State is not ForecastState.Learning and not ForecastState.IdleWithinMeterPrecision;
                            return Make(origin, null, supported ? f.ProjectedRemainingAtResetPercent : null,
                                supported ? f.Evidence?.RemainingAtResetLowerPercent : null,
                                supported ? f.Evidence?.RemainingAtResetUpperPercent : null,
                                f.Evidence?.Model ?? "unavailable", supported ? "" : f.IsQuantizedFlat ? "flat-meter" : "insufficient-history", labels, prefixes);
                        }).ToArray();
                    }
                    cached[effective] = points[configuration.Id];
                }
                AddBaselines(points);
                Summarize(id, cohort.Key.Kind, target, "outlook", origins, points, scores, splits);
            }

            // Origin features use collection-time availability. No target workload enters the request.
            IReadOnlyList<ScenarioHistorySample> samples = CodexScenarioHistoryBuilder.Build(local, ForecastReplayAvailability.CollectedByOrigin);
            var features = new CodexForecastFeatureIndex(local);
            foreach (double hours in new[] { .5, 1d })
            {
                progress?.Invoke($"{id}: scenario {hours:g}h");
                DateTimeOffset[] origins = prefixes.Where(x => x.Key.AddHours(hours) <= x.Value[^1].ResetsAtUtc).Select(x => x.Key).Order().ToArray();
                var labels = Labels(epochs, origins, hours);
                var points = new Dictionary<string, IReadOnlyList<Point>>();
                foreach (ThresholdConfiguration configuration in Configurations)
                {
                    var results = new List<Point>();
                    foreach (DateTimeOffset origin in origins)
                    {
                        token.ThrowIfCancellationRequested();
                        CodexForecastFeatures f = features.Build(origin, ForecastReplayAvailability.CollectedByOrigin);
                        IReadOnlyList<string> originReasons = ScenarioHistoryEligibility.OriginReasons(f);
                        if (originReasons.Count > 0)
                        {
                            results.Add(Make(origin, hours, null, null, null, "unavailable", string.Join("+", originReasons), labels, prefixes));
                            continue;
                        }
                        var request = new ScenarioRequest(hours, f.TokenActiveRootSessions, f.TokenActiveSubagentSessions,
                            AccountKey: cohort.Key.AccountKey) { Cohorts = [cohort.Key] };
                        ScenarioEstimate estimate = QuotaPredictionService.Simulate(request, samples, origin, configuration.Options);
                        ScenarioWindowEstimate window = cohort.Key.Kind == QuotaWindowKind.Weekly ? estimate.Weekly : estimate.FiveHour;
                        double left = prefixes[origin][^1].RemainingPercent!.Value;
                        results.Add(Make(origin, hours, window.ExpectedQuotaDeltaPercent is { } delta ? Math.Max(0, left - delta) : null,
                            window.UpperQuotaDeltaPercent is { } upper ? Math.Max(0, left - upper) : null,
                            window.LowerQuotaDeltaPercent is { } lower ? Math.Max(0, left - lower) : null,
                            window.HasEnoughHistory ? window.SupportReasons.HasFlag(ForecastSupportReason.ChallengerNotBetter) ||
                                window.SupportReasons.HasFlag(ForecastSupportReason.InsufficientHistory) ? "elapsed-time-baseline" : "workload-ridge" : "unavailable",
                            window.HasEnoughHistory ? "" : window.SupportReasons.ToString(), labels, prefixes));
                    }
                    points[configuration.Id] = results;
                }
                AddBaselines(points);
                Summarize(id, cohort.Key.Kind, FormattableString.Invariant($"{hours:g}h"), "scenario", origins, points, scores, splits);
            }
        }
        progress?.Invoke("Composed selection: replaying strict collection-time comparisons");
        StudyComposed(data, scores, splits, token);
        return new(Version, Fingerprint(data), data.CapturedAtUtc, Configurations, splits, scores,
            "Read-only one-factor-at-a-time study; released defaults unchanged. Candidate origins are sampled at least 30 minutes apart after 15 minutes of compatible history, before reset. " +
            "Final 20% of candidate origins are withheld from configuration choice; development labels must mature before the split. Each prediction sees only earlier available outcomes. " +
            "Scores remain separate by known account/source/plan/limit/window cohort and target; aliases reveal no account identifiers. Fixed-horizon outcomes, blocks and completed reset cycles are separate evidence units. " +
            "Point and band availability share the candidate-origin denominator; unmatched predictions are not accuracy observations. Baseline errors use identical paired targets. " +
            "Band coverage uses banded labeled outcomes only. Non-overlapping outcome and block counts are reported separately; spacing does not prove independence. " +
            "Scenario requests use origin-time local session counts with model/effort unconstrained. Composed availability is a conservative replay lower bound on constructed targets: missing target/composition is not proof of a live abstention. " +
            "A configuration is chosen on development paired MAE only if point availability is no worse than current; ties retain current. Fewer than 20 non-overlapping final outcomes is inconclusive, not a new production gate. " +
            "No automatic promotion; evaluate error, availability, range coverage/width and provenance together before changing product policy.");
    }

    // Target lookup is independent of the production training sampler. A skipped earlier target
    // must not shift label sampling and turn later observed outcomes into artificial missing data.
    private static IReadOnlyDictionary<DateTimeOffset, QuotaForecastTrial> Labels(
        IReadOnlyList<IReadOnlyList<QuotaSnapshot>> epochs, IReadOnlyList<DateTimeOffset> origins, double? horizon)
    {
        var requested = origins.ToHashSet();
        var result = new Dictionary<DateTimeOffset, QuotaForecastTrial>();
        foreach (IReadOnlyList<QuotaSnapshot> epoch in epochs)
        foreach (QuotaSnapshot anchor in epoch.Where(x => requested.Contains(x.CapturedAtUtc)))
        {
            DateTimeOffset target = horizon is { } hours ? anchor.CapturedAtUtc.AddHours(hours) : anchor.ResetsAtUtc!.Value;
            QuotaSnapshot? actual = horizon is null ? epoch[^1] : epoch.FirstOrDefault(x => x.CapturedAtUtc >= target);
            if (actual is null || actual.CapturedAtUtc <= anchor.CapturedAtUtc || Math.Abs((actual.CapturedAtUtc - target).TotalMinutes) > 5) continue;
            result[anchor.CapturedAtUtc] = new("persistence", horizon is null ? "near-reset-proxy" : "fixed-horizon",
                anchor.CapturedAtUtc, actual.CapturedAtUtc, anchor.ResetsAtUtc!.Value, (actual.CapturedAtUtc - anchor.CapturedAtUtc).TotalHours,
                anchor.RemainingPercent!.Value, actual.RemainingPercent!.Value, null, null, 0, null, false, null);
        }
        return result;
    }

    private static Point Make(DateTimeOffset origin, double? horizon, double? prediction, double? low, double? high, string model, string reason,
        IReadOnlyDictionary<DateTimeOffset, QuotaForecastTrial> labels, IReadOnlyDictionary<DateTimeOffset, QuotaSnapshot[]> prefixes)
    {
        QuotaSnapshot[] epoch = prefixes[origin];
        QuotaSnapshot anchor = epoch[^1];
        labels.TryGetValue(origin, out QuotaForecastTrial? label);
        double lead = horizon ?? (anchor.ResetsAtUtc!.Value - origin).TotalHours;
        return new(origin, anchor.ResetsAtUtc!.Value, prediction, label?.ObservedRemaining, label?.OutcomeUtc, low, high,
            anchor.RemainingPercent, QuotaPaceModels.ProjectRemaining(epoch, QuotaPredictionService.FallbackModel, lead), model, reason);
    }

    private static void AddBaselines(Dictionary<string, IReadOnlyList<Point>> points)
    {
        Point[] source = points["current"].ToArray();
        points["persistence"] = source.Select(x => x with { Prediction = x.Persistence, Lower = null, Upper = null, Model = "persistence", Reason = "" }).ToArray();
        points["simple-pace"] = source.Select(x => x with { Prediction = x.Pace, Lower = null, Upper = null, Model = "simple-pace", Reason = "" }).ToArray();
    }

    private static void Summarize(string cohort, QuotaWindowKind kind, string target, string family, DateTimeOffset[] origins,
        IReadOnlyDictionary<string, IReadOnlyList<Point>> points, List<ThresholdStudyScore> scores, List<ThresholdStudySplit> splits)
    {
        if (origins.Length == 0)
        {
            splits.Add(new(cohort, family, target, null, "current", "inconclusive: no candidate origins"));
            return;
        }
        DateTimeOffset split = origins[(int)Math.Floor(origins.Length * .8)];
        Point[] Development(string configuration) => points[configuration].Where(x => x.Origin < split && (x.Outcome is null || x.Outcome < split)).ToArray();
        int currentAvailable = Development("current").Count(x => x.Prediction is not null);
        var current = Development("current").Where(x => x.Actual is not null && x.Prediction is not null).ToDictionary(x => x.Origin);
        double Improvement(string configuration)
        {
            Point[] paired = Development(configuration).Where(x => x.Prediction is not null && x.Actual is not null && current.ContainsKey(x.Origin)).ToArray();
            return paired.Length < 20 ? 0 : paired.Average(x => Math.Abs(current[x.Origin].Prediction!.Value - x.Actual!.Value) - Math.Abs(x.Prediction!.Value - x.Actual.Value));
        }
        string choice = Configurations.Where(x => Development(x.Id).Count(p => p.Prediction is not null) >= currentAvailable)
            .OrderByDescending(x => Improvement(x.Id)).ThenBy(x => x.Id == "current" ? 0 : 1).First().Id;
        int finalPairs = ChronologicalEvidence.Blocks(points[choice].Where(x => x.Origin >= split && x.Prediction is not null && x.Actual is not null),
            x => x.Origin, x => x.Outcome!.Value, x => x.Reset).Sum(x => x.Count);
        splits.Add(new(cohort, family, target, split, choice, finalPairs < 20 ? "inconclusive: fewer than 20 non-overlapping final outcomes" : "final comparison available; no production change"));
        foreach ((string configuration, IReadOnlyList<Point> values) in points)
        foreach (string period in new[] { "development", "final" })
        {
            Point[] selected = values.Where(x => period == "final" ? x.Origin >= split : x.Origin < split).ToArray();
            Point[] predicted = selected.Where(x => x.Prediction is not null).ToArray();
            Point[] paired = predicted.Where(x => x.Actual is not null && (period == "final" || x.Outcome < split)).ToArray();
            Point[] bands = paired.Where(x => x.Lower is not null && x.Upper is not null).ToArray();
            var blocks = ChronologicalEvidence.Blocks(paired, x => x.Origin, x => x.Outcome!.Value, x => x.Reset);
            double? Mean(IEnumerable<double> numbers) { double[] n = numbers.ToArray(); return n.Length == 0 ? null : n.Average(); }
            scores.Add(new(cohort, kind, target, family, configuration, period, selected.Length, predicted.Length,
                predicted.Count(x => x.Lower is not null && x.Upper is not null), paired.Length, blocks.Sum(x => x.Count), blocks.Count, bands.Length, predicted.Length - paired.Length,
                Mean(paired.Select(x => Math.Abs(x.Prediction!.Value - x.Actual!.Value))),
                Mean(paired.Select(x => Math.Pow(x.Prediction!.Value - x.Actual!.Value, 2))) is { } mse ? Math.Sqrt(mse) : null,
                Mean(paired.Where(x => x.Persistence is not null).Select(x => Math.Abs(x.Persistence!.Value - x.Actual!.Value))),
                Mean(paired.Where(x => x.Pace is not null).Select(x => Math.Abs(x.Pace!.Value - x.Actual!.Value))),
                Mean(bands.Select(x => x.Actual >= x.Lower && x.Actual <= x.Upper ? 1d : 0d)),
                Mean(bands.Select(x => x.Upper!.Value - x.Lower!.Value)),
                selected.Select(x => x.Origin.UtcDateTime.Date).Distinct().Count(), QuotaResetGenerationPolicy.Group(selected, x => x.Reset).Count,
                predicted.GroupBy(x => x.Model).ToDictionary(x => x.Key, x => x.Count()),
                selected.Where(x => x.Prediction is null).GroupBy(x => x.Reason).ToDictionary(x => x.Key, x => x.Count())));
        }
    }

    private static void StudyComposed(CodexForecastDataset data, List<ThresholdStudyScore> scores, List<ThresholdStudySplit> splits, CancellationToken token)
    {
        ComposedQuotaEvaluation evaluation = ComposedQuotaValidation.Evaluate(data, token, ForecastReplayAvailability.CollectedByOrigin, [.5, 2d]);
        IReadOnlyList<QuotaCostObservation> observations = QuotaCostObservationBuilder.Build(data, token, horizons: [.5, 2d]);
        int cohortIndex = 0;
        foreach (IGrouping<(QuotaHistoryCohort Cohort, double HorizonHours), ComposedQuotaScore> group in evaluation.Scores
                     .Where(x => x.Cohort.AccountKey is not null).GroupBy(x => (x.Cohort, x.HorizonHours)))
        {
            string id = $"composed-cohort-{++cohortIndex:00}";
            QuotaCostObservation[] rows = observations.Where(x => x.Cohort == group.Key.Cohort && x.HorizonHours == group.Key.HorizonHours).OrderBy(x => x.StartUtc).ToArray();
            var points = new Dictionary<string, IReadOnlyList<Point>>();
            foreach (ThresholdConfiguration configuration in Configurations)
            {
                var results = new List<Point>();
                foreach (QuotaCostObservation row in rows)
                {
                    token.ThrowIfCancellationRequested();
                    var candidates = new List<(ComposedQuotaScore Score, ComposedQuotaTrial Trial, double Loss)>();
                    foreach (ComposedQuotaScore full in group)
                    {
                        ComposedQuotaTrial[] past = full.Trials.Where(x => x.OutcomeUtc <= row.StartUtc && x.CalibrationAvailableAtUtc <= row.StartUtc).ToArray();
                        // Only outcome opportunities already available at the origin may count as withheld.
                        int missing = rows.Skip(20).Count(x => x.EndUtc <= row.StartUtc && x.OutcomeCollectedAtUtc <= row.StartUtc &&
                            !past.Any(p => p.OriginUtc == x.StartUtc));
                        ComposedSelectionAssessment assessment = ComposedQuotaPolicy.AssessSelection(full with
                        { Trials = past, HeldOutIntervals = past.Length, MissingComposition = missing }, row.StartUtc, true, configuration.Options);
                        ComposedQuotaTrial? trial = full.Trials.FirstOrDefault(x => x.OriginUtc == row.StartUtc);
                        if (assessment.Passed && trial is not null && assessment.BlockBalancedLoss is { } loss)
                            candidates.Add((full, trial, loss));
                    }
                    var chosen = candidates.OrderBy(x => x.Loss).ThenBy(x => x.Score.CostModel).FirstOrDefault();
                    (double? Radius, int Blocks) band = chosen.Score is not null ? ComposedQuotaPolicy.CalibrateUncertainty(
                        chosen.Score.Trials, row.StartUtc, configuration.Options) : (null, 0);
                    double? prediction = chosen.Trial?.PredictedRemaining;
                    results.Add(new(row.StartUtc, row.ResetUtc, prediction, 100 - row.EndUsed, row.EndUtc,
                        prediction is { } p && band.Radius is { } radius ? Math.Max(0, p - radius) : null,
                        prediction is { } upper && band.Radius is { } r ? Math.Min(100 - row.StartUsed, upper + r) : null,
                        100 - row.StartUsed, Math.Clamp(100 - row.StartUsed - row.PaceDelta, 0, 100),
                        chosen.Score?.CostModel ?? "unavailable", "strict-selection-or-composition-unavailable"));
                }
                points[configuration.Id] = results;
            }
            AddBaselines(points);
            Summarize(id, group.Key.Cohort.Kind, FormattableString.Invariant($"{group.Key.HorizonHours:g}h"), "composed-selection", rows.Select(x => x.StartUtc).ToArray(), points, scores, splits);
        }
    }

    public static string Fingerprint(CodexForecastDataset data)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add<T>(string name, IEnumerable<T> rows)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            foreach (T row in rows) hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row));
        }
        Add("quota", data.Quota); Add("tokens", data.Tokens); Add("workload", data.Workload); Add("context", data.Context);
        Add("associations", data.AccountAssociations);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
