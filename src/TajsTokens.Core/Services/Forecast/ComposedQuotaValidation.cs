// Taj's Tokens | ComposedQuotaValidation.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

/// <summary>Actual-cost and origin-only forecast errors on identical disjoint outcomes.</summary>
public static partial class ComposedQuotaValidation
{
    public const string Version = "composed-quota/v12-blocks";
    public static IReadOnlyList<double> EvaluationHorizons { get; } = Array.AsReadOnly(new[] { 5d / 60, .25, .5, 2d });

    public static ComposedQuotaEvaluation Evaluate(
        CodexForecastDataset data,
        CancellationToken cancellationToken = default,
        ForecastReplayAvailability availability = ForecastReplayAvailability.ReconstructedEventTime,
        IReadOnlyList<double>? horizons = null,
        bool includeJointResearch = false,
        bool includeJointPathsResearch = false)
    {
        IReadOnlyList<QuotaCostObservation> rows = QuotaCostObservationBuilder.Build(
            data,
            cancellationToken,
            horizons: horizons ?? EvaluationHorizons);
        var predictions = new Dictionary<(DateTimeOffset, double), PredictedWorkload?>();
        var tokenReplay = new TokenWorkloadPredictionService.ReplayCache();
        var activities = new Dictionary<DateTimeOffset, string>();
        CodexForecastFeatureIndex? continuationIndex = includeJointPathsResearch ? new(data) : null;
        WorkloadCompositionPrediction.PathCache? pathCache = includeJointPathsResearch ? new() : null;
        var scores = new List<ComposedQuotaScore>();
        foreach (IGrouping<(QuotaHistoryCohort Cohort, double HorizonHours), QuotaCostObservation> group in rows.GroupBy(x =>
                     (x.Cohort, x.HorizonHours)))
        {
            QuotaCostObservation[] ordered = group.OrderBy(x => x.StartUtc).ToArray();
            QuotaCostObservation[] nativeTraining = ordered.Take(20).ToArray();
            QuotaSnapshot[] quota = QuotaHistoryPolicy.ReplayRows(
                QuotaHistoryPolicy.Streams(
                    QuotaHistoryPolicy.Describe(
                        QuotaHistoryPolicy.AvailableRows(
                            data.Quota.Where(x => QuotaHistoryPolicy.Cohort(x) == group.Key.Cohort),
                            availability),
                        data.CapturedAtUtc)).SelectMany(x => x));
            IReadOnlyDictionary<DateTimeOffset, QuotaHorizonPrediction>? incumbent = null;

            IReadOnlyDictionary<DateTimeOffset, QuotaHorizonPrediction> ReadIncumbent()
            {
                // Withheld trials have no comparison to score. In particular, late-collected
                // training must not trigger a full replay that will never be used.
                return incumbent ??= QuotaPredictionService.ReplayAtTargets(
                    data with { Quota = quota },
                    group.Key.HorizonHours,
                    availability,
                    ordered.Skip(20).Select(x => (x.StartUtc, x.EndUtc)).ToArray(),
                    cancellationToken);
            }
            QuotaCostObservation[] asserted = QuotaCostTrainingPolicy.Select(rows, group.Key.Cohort, group.Key.HorizonHours, true);
            string[] candidates = includeJointPathsResearch ? ["total", "categories", "model-effort", "joint-categories",
                "joint-recent-30m", "joint-recent-2h", "joint-blocks-30m", "joint-blocks-2h"] :
                includeJointResearch ? ["total", "categories", "model-effort", "joint-categories"] : ["total", "categories", "model-effort"];
            foreach (string candidate in asserted.Length > nativeTraining.Length
                         ? candidates.Concat(candidates.Select(x => "asserted-" + x))
                         : candidates)
            {
                QuotaCostObservation[] training = candidate.StartsWith("asserted-", StringComparison.Ordinal) ? asserted : nativeTraining;
                var trials = new List<ComposedQuotaTrial>();
                int missing = 0;
                var withheldReasons = new Dictionary<string, int>();

                void Withheld(string reason)
                {
                    withheldReasons[reason] = withheldReasons.GetValueOrDefault(reason) + 1;
                }

                string baseCandidate = candidate.Replace("asserted-", "", StringComparison.Ordinal);
                bool pathCandidate = baseCandidate.StartsWith("joint-recent-", StringComparison.Ordinal) || baseCandidate.StartsWith("joint-blocks-", StringComparison.Ordinal);
                bool needsCategories = baseCandidate != "total";
                bool completeTraining = !needsCategories || training.All(x => x.HasCompleteTokenCategories);
                bool joint = baseCandidate == "joint-categories" || pathCandidate;
                bool completeJointTraining = !joint || training.All(QuotaCostIdentification.Valid);
                bool hasTrainingWork = QuotaAccountingModel.HasTrainingWork(training);
                if (training.Length >= 20 && !hasTrainingWork) withheldReasons["no-recorded-training-work"] = training.Length;
                if (!completeTraining) withheldReasons["incomplete-category-training"] = training.Count(x => !x.HasCompleteTokenCategories);
                if (!completeJointTraining) withheldReasons["incomplete-joint-training"] = training.Count(x => !QuotaCostIdentification.Valid(x));
                if (nativeTraining.Length == 20 && completeTraining && completeJointTraining && hasTrainingWork)
                {
                    Func<QuotaCostObservation, double> fit = QuotaAccountingModel.FitFrozen(
                        training,
                        joint ? "joint-categories" : baseCandidate,
                        cancellationToken);
                    var inputCheck = pathCandidate ? new JointPathInputCheck(training, fit) : null;
                    foreach (QuotaCostObservation row in ordered.Skip(20).Where(x => x.StartUtc >= training[^1].EndUtc))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (needsCategories && !row.HasCompleteTokenCategories)
                        {
                            Withheld("incomplete-category-outcome");
                            continue;
                        }
                        if (joint && !QuotaCostIdentification.Valid(row))
                        {
                            Withheld("incomplete-joint-outcome");
                            continue;
                        }
                        if (availability == ForecastReplayAvailability.CollectedByOrigin)
                        {
                            bool unavailable = false;

                            void Check(bool failed, string reason)
                            {
                                if (!failed) return;
                                unavailable = true;
                                Withheld(reason);
                            }

                            // All supported composed cost candidates use only token amounts/model/effort.
                            // Oracle context/activity/runtime ablations are not composed candidates.
                            Check(training.Any(x => x.TokenCostEvidenceAvailableAtUtc is null), "training-collection-unknown");
                            Check(training.Any(x => x.TokenCostEvidenceAvailableAtUtc > row.StartUtc), "training-collected-after-origin");
                            Check(row.OriginCollectedAtUtc is null, "origin-meter-collection-unknown");
                            Check(row.OriginCollectedAtUtc > row.StartUtc, "origin-meter-collected-after-origin");
                            Check(row.OriginEvidenceAvailableAtUtc is null, "origin-prefix-collection-unknown");
                            Check(row.OriginEvidenceAvailableAtUtc > row.StartUtc, "origin-prefix-collected-after-origin");
                            Check(row.OutcomeCollectedAtUtc is null, "outcome-meter-collection-unknown");
                            Check(row.OutcomeCollectedAtUtc > row.EndUtc.AddMinutes(5), "outcome-meter-collected-too-late");
                            if (unavailable)
                            {
                                missing++;
                                continue;
                            }
                        }
                        // Use the actual elapsed target horizon, never the target's workload.
                        double hours = (row.EndUtc - row.StartUtc).TotalHours;
                        (DateTimeOffset StartUtc, double hours) key = (row.StartUtc, hours);
                        PredictedWorkload? workload;
                        JointWorkloadPaths? ensemble = null;
                        double contextHours = baseCandidate.EndsWith("-30m", StringComparison.Ordinal) ? .5 : 2;
                        if (pathCandidate)
                        {
                            if (baseCandidate.StartsWith("joint-blocks-", StringComparison.Ordinal))
                            {
                                ensemble = WorkloadCompositionPrediction.SamplePaths(continuationIndex!, row.StartUtc, contextHours, hours, availability,
                                    WorkloadCompositionPrediction.DefaultPathSeed, pathCache!, cancellationToken);
                                workload = ensemble.Mean is { } mean ? DescribeJoint(mean, ensemble.Context with
                                { Version = WorkloadCompositionPrediction.PathVersion, ProjectedTokens = (double)mean.ReportedTokens,
                                    Status = "conditional-block-path-mean", Methodology = WorkloadCompositionPrediction.PathMethodology }) : null;
                            }
                            else workload = WorkloadCompositionPrediction.Continue(continuationIndex!, row.StartUtc, contextHours, hours, availability).Workload;
                        }
                        else if (!predictions.TryGetValue(key, out workload))
                        {
                            workload = TokenWorkloadPredictionService.PredictHorizonCached(
                                data,
                                row.StartUtc,
                                hours,
                                availability,
                                cancellationToken,
                                tokenReplay)?.Composition;
                            predictions.Add(key, workload);
                        }
                        if (workload is null)
                        {
                            missing++;
                            Withheld("missing-or-stale-composition");
                            continue;
                        }
                        if (joint && workload.JointWorkload is not { IsComplete: true })
                        {
                            missing++;
                            Withheld("missing-joint-continuation");
                            continue;
                        }
                        QuotaCostObservation projected = Project(row, workload);
                        double predicted = fit(projected);
                        double actualCost = fit(row);
                        JointQuotaPathEvidence? pathEvidence = pathCandidate ? inputCheck!.Describe(workload.JointWorkload!, contextHours,
                            ensemble, part => fit(Project(row, DescribeJoint(part, workload.Continuation!)))) : null;
                        if (!activities.TryGetValue(row.StartUtc, out string? activity))
                        {
                            activity = CodexNowcastActivity.Evaluate(data, row.StartUtc, availability).State.ToString();
                            activities.Add(row.StartUtc, activity);
                        }
                        (double? Radius, int Blocks) calibration = ComposedQuotaPolicy.CalibrateUncertainty(trials, row.StartUtc);
                        double remaining = Math.Clamp(100 - row.StartUsed - predicted, 0, 100 - row.StartUsed);
                        trials.Add(
                            new ComposedQuotaTrial(
                                row.StartUtc,
                                row.EndUtc,
                                row.ResetUtc,
                                predicted,
                                actualCost,
                                row.ObservedDelta,
                                row.IntervalLoss(predicted),
                                row.IntervalLoss(actualCost),
                                row.IntervalLoss(row.PaceDelta),
                                Math.Clamp(100 - row.StartUsed - predicted, 0, 100),
                                workload)
                            {
                                Availability = availability,
                                OriginActivity = activity,
                                RecordedOutcomeTokens = row.Features.Tokens,
                                ZeroUseIntervalLoss = row.IntervalLoss(0),
                                LowerObservedDelta = row.LowerDelta,
                                UpperObservedDelta = row.UpperDelta,
                                OutcomeQualityFlags = row.QualityFlags.ToArray(),
                                CompleteOutcomeTokenCategories = row.HasCompleteTokenCategories,
                                JointPath = pathEvidence,
                                CalibrationAvailableAtUtc = availability == ForecastReplayAvailability.CollectedByOrigin
                                    ? row.OutcomeCollectedAtUtc
                                    : row.EndUtc,
                                ObservedRemainingPercent = 100 - row.EndUsed,
                                LowerRemainingPercent = calibration.Radius is { } low ? Math.Max(0, remaining - low) : null,
                                UpperRemainingPercent =
                                    calibration.Radius is { } high ? Math.Min(100 - row.StartUsed, remaining + high) : null,
                                CalibrationBlocks = calibration.Blocks,
                                IncumbentIntervalLoss = ReadIncumbent().TryGetValue(row.StartUtc, out QuotaHorizonPrediction? baseline) &&
                                                        baseline.TargetUtc == row.EndUtc
                                    ? row.IntervalLoss(100 - row.StartUsed - baseline.RemainingPercent)
                                    : null,
                            });
                    }
                }
                ComposedQuotaTrial[] bands = trials.Where(x => x.LowerRemainingPercent is not null && x.UpperRemainingPercent is not null)
                    .ToArray();
                scores.Add(
                    new ComposedQuotaScore(
                        group.Key.Cohort,
                        group.Key.HorizonHours,
                        candidate,
                        training.Length,
                        trials.Count,
                        QuotaResetGenerationPolicy.Group(trials, x => x.ResetUtc).Count,
                        missing,
                        Mean(trials.Select(x => x.IntervalLoss)),
                        Mean(trials.Select(x => x.CostOnlyIntervalLoss)),
                        Mean(trials.Select(x => x.PaceIntervalLoss)),
                        Mean(trials.Select(x => Math.Abs(x.PredictedDelta - x.ObservedDelta))),
                        trials)
                    {
                        Availability = availability,
                        Breakdowns = ComposedQuotaBreakdowns.Build(trials),
                        IntervalOrigins = bands.Length,
                        IntervalCoverage = Mean(
                            bands.Select(x => x.ObservedRemainingPercent >= x.LowerRemainingPercent &&
                                              x.ObservedRemainingPercent <= x.UpperRemainingPercent
                                ? 1d
                                : 0d)),
                        MeanIntervalWidth = Mean(bands.Select(x => x.UpperRemainingPercent!.Value - x.LowerRemainingPercent!.Value)),
                        AssertedTrainingIntervals = training.Count(x => x.Attribution == QuotaAccountAttribution.UserAsserted),
                        WithheldReasons = withheldReasons,
                        IncumbentIntervalLoss = trials.All(x => x.IncumbentIntervalLoss is not null)
                            ? Mean(trials.Select(x => x.IncumbentIntervalLoss!.Value))
                            : null,
                    });
            }
        }
        return new ComposedQuotaEvaluation(
            includeJointPathsResearch ? Version + "/joint-path-research-v1" : includeJointResearch ? Version + "/joint-research-v1" : Version,
            $"{availability}: native token forecast at each exact origin, then preceding-two-hour " +
            "composition, then frozen first-20-interval cost weights. Forecast, actual-workload cost, and pace losses use identical held-out targets. " +
            "CollectedByOrigin requires frozen token-cost inputs (quota labels, token amounts/model/effort and any ownership assertions) collected before the origin, the origin meter already collected, " +
            "and an outcome meter collected within five minutes of its event; token prediction uses origin-time availability. " +
            "Unused activity/context/tier backfills do not change token-cost training availability. Withheld reason counts can overlap. " +
            "Backfilled history alone cannot qualify. No future workload/model/context features enter predictions. " +
            "Missing/stale composition is withheld, not zero. Joint workload/cost ranges use the live policy's completed-block maximum " +
            "absolute displayed-delta errors, with eight earlier blocks, an empirical 80% target and a one-point minimum radius. " +
            "Calibration labels must already be available at the origin (event time for reconstruction, actual collection for strict replay). " +
            "Coverage measures reported remaining quota, not latent true usage or exhaustion probability. Sparse ranges remain absent. " +
            "No activity-conditioned quota distribution or live promotion is implied. " +
            (includeJointResearch || includeJointPathsResearch ? "Explicit joint research uses the existing origin-only joint continuation with nonnegative coefficients shrunk toward the frozen pooled-category fit. It cannot enter live selection. " : "") +
            (includeJointPathsResearch ? "Explicit joint-path research compares whole recent-context continuation and 64 contiguous-block paths for 30-minute/two-hour contexts, using the same frozen joint-cost fit. " +
                "Input/fit compatibility is reported separately; failed checks do not silently suppress exploratory errors or qualify release. " +
                "Raw path P10/P90 are model-conditional and separate from earlier-held-out residual bands. " + WorkloadCompositionPrediction.PathMethodology + " " : "") +
            ComposedQuotaBreakdowns.Methodology,
            scores) { PathCache = pathCache is null ? null : new(pathCache.Count, WorkloadCompositionPrediction.PathCache.MaximumEntries, pathCache.Hits, pathCache.Misses) };
    }

    internal static QuotaCostObservation Project(QuotaCostObservation row, PredictedWorkload workload)
    {
        return row with
        {
            TokenCategories = workload.TokenCategories,
            Features = row.Features with
            {
                Tokens = (long)Math.Clamp(workload.ExpectedTokens, 0, long.MaxValue),
                ModelTokenShares = workload.ModelShares,
                EffortTokenShares = workload.EffortShares,
                JointWorkload = workload.JointWorkload,
            },
        };
    }

    private static double? Mean(IEnumerable<double> values)
    {
        double[] rows = values.ToArray();
        return rows.Length == 0 ? null : rows.Average();
    }
}
