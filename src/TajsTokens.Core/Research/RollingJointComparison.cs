using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Research;

public sealed record RollingJointTrial(string Candidate, DateTimeOffset OriginUtc, DateTimeOffset TargetUtc,
    DateTimeOffset OutcomeUtc, DateTimeOffset ResetUtc, string Period, double? Remaining, double ObservedRemaining, double AnchorRemaining,
    double DirectRemaining, double? HeadRemaining, double? LowerRemaining, double? UpperRemaining,
    int TrainingRows, DateTimeOffset? TrainingFromUtc, DateTimeOffset? TrainingToUtc,
    DateTimeOffset? TrainingAvailableUtc, string? WithheldReason, double LowerDelta, double UpperDelta,
    double? IntervalLoss, double? PathLowerRemaining, double? PathUpperRemaining,
    DateTimeOffset? OriginalMeasuredOriginUtc, DateTimeOffset? OriginalMeasuredOutcomeUtc);

public sealed record RollingJointScore(string Candidate, string Period, int CandidateOrigins, int PointOrigins,
    int MatchedHeadOrigins, int BandOrigins, int BandedHeadOrigins, int RawPathHeadOrigins, int NonOverlappingOutcomes, int Blocks,
    double? PairedMae, double? HeadMae, double? PairedIntervalLoss, double? HeadIntervalLoss,
    double? SignedErrorSum, double? IntervalCoverage, double? MeanIntervalWidth,
    double? RawPathCoverage, double? RawPathWidth, IReadOnlyDictionary<string, int> WithheldReasons);

public sealed record RollingJointCohort(string Cohort, string Window, bool KnownAccount, string Authority, double HorizonHours, int SourceIntervals,
    int ExaminedIntervals, DateTimeOffset? SplitUtc, string DevelopmentChoice, IReadOnlyList<string> ChoiceReasons,
    IReadOnlyList<RollingJointScore> Scores, IReadOnlyList<RollingJointScore> PolicyScores,
    RollingJointScore FinalPolicy, int FinalFallbackOrigins,
    IReadOnlyList<RollingJointTrial> Trials);

public sealed record RollingJointReport(string Version, string EvidenceIdentity, ForecastReplayAvailability Availability,
    DateTimeOffset CutoffUtc, int InputQuotaRows, int ReplayedQuotaRows, int ClockQualifiedQuotaRows,
    int ConstructedIntervals, int ConstructionStarts, IReadOnlyList<RollingJointCohort> Cohorts, string Boundary);

public sealed record RollingJointFitAudit(string Cohort, string Candidate, DateTimeOffset OriginUtc, double HorizonHours,
    IReadOnlyList<QuotaCostObservation> Training, CodexJointWorkload Projected, double PredictedDelta);

public static class RollingJointComparison
{
    public const string RollingJointVersion = "rolling-joint-comparison/v1";
    public static IReadOnlyList<string> RollingJointCandidates { get; } = Array.AsReadOnly(new[]
    {
        "direct", "released-head", "first20-joint-2h", "prefix-categories-30m", "prefix-categories-2h",
        "prefix-joint-30m", "prefix-joint-2h", "prefix-blocks-2h",
    });

    /// <summary>Explicit research; fixed nominal targets and development-only policy choice.</summary>
    public static RollingJointReport Evaluate(CodexForecastDataset source,
        ForecastReplayAvailability availability, CancellationToken token = default,
        Action<RollingJointFitAudit>? audit = null, bool includeTrials = false)
    {
        if (!Enum.IsDefined(availability)) throw new ArgumentException("Unknown replay availability.");
        bool strict = availability == ForecastReplayAvailability.CollectedByOrigin;
        var quota = QuotaHistoryPolicy.AvailableRows(source.Quota, availability).Where(q => q.CapturedAtUtc <= source.CapturedAtUtc)
            .Select(q => strict && (q.CollectedAtUtc is null || q.CollectedAtUtc < (q.ReplayMeasuredAtUtc ?? q.CapturedAtUtc) ||
                q.CollectedAtUtc > (q.ReplayMeasuredAtUtc ?? q.CapturedAtUtc).AddMinutes(5)) ? q with { UsedPercent = null } : q).ToArray();
        var data = source with { Quota = quota };
        var built = QuotaCostObservationBuilder.BuildDetailed(data, token, horizons: ComposedQuotaValidation.EvaluationHorizons);
        var featureIndex = new CodexForecastFeatureIndex(data);
        var result = new List<RollingJointCohort>();
        foreach (var group in built.Observations.GroupBy(r => (r.Cohort, r.HorizonHours)))
        {
            token.ThrowIfCancellationRequested();
            string alias = "cohort-" + (result.Count + 1);
            var rows = group.OrderBy(r => r.StartUtc).Take(QuotaCostIdentification.MaximumRows).ToArray();
            var targets = rows.Skip(20).ToArray();
            var clocks = quota.Where(q => QuotaHistoryPolicy.Cohort(q) == group.Key.Cohort).Select(q => q.CapturedAtUtc).ToArray();
            long span = (clocks.Max() - clocks.Min()).Ticks;
            DateTimeOffset? split = targets.Length == 0 ? null : clocks.Min().AddTicks(span / 5 * 4 + span % 5 * 4 / 5);
            var decisions = QuotaHistoryPolicy.Describe(quota.Where(q => QuotaHistoryPolicy.SameMeter(QuotaHistoryPolicy.Cohort(q), group.Key.Cohort)), data.CapturedAtUtc);
            var meter = QuotaHistoryPolicy.PaceRows(QuotaHistoryPolicy.ReplayRows(decisions.Where(d =>
                d.Reason is not "same-time-repeat" and not "possibly-cached-repeat")), group.Key.Cohort);
            var byOrigin = QuotaForecastCalibration.SplitEpochs(meter).SelectMany(epoch => epoch.Select((q, i) =>
                (q.CapturedAtUtc, Epoch: epoch, Index: i))).ToDictionary(x => x.CapturedAtUtc);
            var head = targets.Length == 0 ? new Dictionary<DateTimeOffset, QuotaHorizonPrediction>() :
                QuotaPredictionService.ReplayOrigins(data with { Quota = meter }, group.Key.HorizonHours, availability,
                    targets.Select(r => r.StartUtc).ToArray(), token);
            var fits = new Dictionary<(string Mode, string Model, int Count), Func<QuotaCostObservation, double>>();
            var histories = RollingJointCandidates.ToDictionary(c => c, _ => new List<ComposedQuotaTrial>());
            var audited = new HashSet<string>(StringComparer.Ordinal);
            var pathCache = new WorkloadCompositionPrediction.PathCache();
            var trials = new List<RollingJointTrial>();
            foreach (var row in targets)
            {
                token.ThrowIfCancellationRequested();
                string period = row.StartUtc < split ? row.EndUtc <= split ? "development" : "boundary" : "final";
                var location = byOrigin[row.StartUtc];
                var prefix = new ArraySegment<QuotaSnapshot>(location.Epoch.ToArray(), 0, location.Index + 1);
                double direct = QuotaPaceModels.ProjectRemaining(prefix, QuotaPaceModels.ProductionFallback, row.HorizonHours);
                head.TryGetValue(row.StartUtc, out var incumbent);
                var expanding = rows.Where(r => r.EndUtc <= row.StartUtc && (!strict || r.TokenCostEvidenceAvailableAtUtc <= row.StartUtc)).ToArray();
                var first = rows.Take(20).ToArray();
                var observedOrigin = location.Epoch[location.Index];
                var observedOutcome = location.Epoch.First(q => q.CapturedAtUtc == row.EndUtc);
                foreach (string candidate in RollingJointCandidates)
                {
                    double? remaining = null, low = null, high = null, pathLow = null, pathHigh = null;
                    string? reason = null;
                    QuotaCostObservation[] training = [];
                    if (strict && (row.OriginCollectedAtUtc is null || row.OriginCollectedAtUtc > row.StartUtc ||
                        row.OriginEvidenceAvailableAtUtc is null || row.OriginEvidenceAvailableAtUtc > row.StartUtc ||
                        row.OutcomeCollectedAtUtc is null || row.OutcomeCollectedAtUtc > row.EndUtc.AddMinutes(5)))
                        reason = "meter-availability";
                    else if (candidate == "direct") remaining = direct;
                    else if (candidate == "released-head")
                    {
                        remaining = incumbent?.RemainingPercent; low = incumbent?.LowerRemainingPercent; high = incumbent?.UpperRemainingPercent;
                        if (remaining is null) reason = "head-unavailable";
                    }
                    else
                    {
                        bool frozen = candidate.StartsWith("first20", StringComparison.Ordinal);
                        training = frozen ? first : expanding;
                        bool categories = candidate.StartsWith("prefix-categories-", StringComparison.Ordinal);
                        if (training.Length < 20) reason = "insufficient-mature-training";
                        else if (training.Any(r => r.EndUtc > row.StartUtc || strict && (r.TokenCostEvidenceAvailableAtUtc is null || r.TokenCostEvidenceAvailableAtUtc > row.StartUtc)))
                            reason = "training-not-available";
                        else if (!QuotaAccountingModel.HasTrainingWork(training)) reason = "no-recorded-training-work";
                        else if (training.Any(r => !r.HasCompleteTokenCategories || !categories && !QuotaCostIdentification.Valid(r))) reason = "incomplete-training-quantities";
                        else
                        {
                            string model = categories ? "categories" : "joint-categories";
                            var key = (frozen ? "frozen" : "prefix", model, training.Length);
                            if (!fits.TryGetValue(key, out var fit))
                            {
                                fit = QuotaAccountingModel.FitFrozen(training, model, token); fits.Add(key, fit);
                            }
                            double context = candidate.EndsWith("30m", StringComparison.Ordinal) ? .5 : 2;
                            JointWorkloadPaths? ensemble = candidate == "prefix-blocks-2h"
                                ? WorkloadCompositionPrediction.SamplePaths(featureIndex, row.StartUtc, context, row.HorizonHours,
                                    availability, WorkloadCompositionPrediction.DefaultPathSeed, pathCache, token) : null;
                            var work = ensemble?.Mean is { } mean ? ComposedQuotaValidation.DescribeJoint(mean, ensemble.Context) : candidate == "prefix-blocks-2h" ? null :
                                WorkloadCompositionPrediction.Continue(featureIndex, row.StartUtc, context, row.HorizonHours, availability).Workload;
                            if (work?.JointWorkload is not { IsComplete: true }) reason = "context-unavailable";
                            else
                            {
                                double predicted = fit(ComposedQuotaValidation.Project(row, work));
                                remaining = Math.Clamp(100 - row.StartUsed - predicted, 0, 100 - row.StartUsed);
                                var calibration = ComposedQuotaPolicy.CalibrateUncertainty(histories[candidate], row.StartUtc);
                                if (calibration.Radius is { } radius) { low = Math.Max(0, remaining.Value - radius); high = Math.Min(100 - row.StartUsed, remaining.Value + radius); }
                                if (ensemble is not null)
                                {
                                    var amounts = ensemble.Paths.Select(p => fit(ComposedQuotaValidation.Project(row, ComposedQuotaValidation.DescribeJoint(p.Aggregate, ensemble.Context)))).Order().ToArray();
                                    if (amounts.Length > 0)
                                    {
                                        pathLow = Math.Max(0, 100 - row.StartUsed - amounts[(int)Math.Ceiling(amounts.Length * .9) - 1]);
                                        pathHigh = Math.Max(0, 100 - row.StartUsed - amounts[(int)Math.Ceiling(amounts.Length * .1) - 1]);
                                    }
                                }
                                double displayedDelta = 100 - row.StartUsed - remaining.Value;
                                histories[candidate].Add(new(row.StartUtc, row.EndUtc, row.ResetUtc, displayedDelta, fit(row), row.ObservedDelta,
                                    row.IntervalLoss(displayedDelta), row.IntervalLoss(fit(row)), row.IntervalLoss(100 - row.StartUsed - direct), remaining.Value, work)
                                    { CalibrationAvailableAtUtc = strict ? row.OutcomeCollectedAtUtc : row.EndUtc, Availability = availability });
                                if (audit is not null)
                                {
                                    bool sample = training.Length is 20 or 21 && audited.Add(candidate + ":" + training.Length);
                                    if (period == "final" && audited.Add(candidate + ":final")) sample = true;
                                    if (sample) audit(new(alias, candidate, row.StartUtc, row.HorizonHours, training, work.JointWorkload, predicted));
                                }
                            }
                        }
                    }
                    trials.Add(new(candidate, row.StartUtc, row.StartUtc.AddHours(row.HorizonHours), row.EndUtc, row.ResetUtc,
                        period, remaining, 100 - row.EndUsed, 100 - row.StartUsed, direct, incumbent?.RemainingPercent, low, high, training.Length,
                        training.Length == 0 ? null : training[0].StartUtc, training.Length == 0 ? null : training.Max(r => r.EndUtc),
                        training.Length == 0 || training.Any(r => r.TokenCostEvidenceAvailableAtUtc is null) ? null : training.Max(r => r.TokenCostEvidenceAvailableAtUtc),
                        reason, row.LowerDelta, row.UpperDelta, remaining is { } value ? row.IntervalLoss(100 - row.StartUsed - value) : null,
                        pathLow, pathHigh, observedOrigin.ReplayMeasuredAtUtc ?? observedOrigin.CapturedAtUtc,
                        observedOutcome.ReplayMeasuredAtUtc ?? observedOutcome.CapturedAtUtc));
                }
            }
            // Boundary-overlapping labels cannot choose a development policy.
            var dev = trials.Where(t => t.Period == "development" && t.OutcomeUtc <= split &&
                (!strict || rows.First(r => r.StartUtc == t.OriginUtc).OutcomeCollectedAtUtc <= split)).ToArray();
            var scores = trials.Where(t => t.Period != "development").GroupBy(t => (t.Candidate, t.Period)).Concat(
                dev.GroupBy(t => (t.Candidate, t.Period))).Select(g => RollingScore(g.Key.Candidate, g.Key.Period, g.ToArray())).ToArray();
            var choice = ChooseRollingDevelopment(dev);
            string chosen = choice.Candidate;
            var reasons = choice.Reasons;
            var finalChosen = trials.Where(t => t.Period == "final" && t.Candidate == chosen).ToArray();
            var finalPolicy = WithHeadFallback(trials.Where(t => t.Period == "final").ToArray(), chosen);
            var policyScores = RollingJointCandidates.SelectMany(candidate => new[] { "development", "final" }.Select(period =>
            {
                var local = period == "development" ? dev : trials.Where(t => t.Period == "final").ToArray();
                return RollingScore(candidate, period, WithHeadFallback(local, candidate));
            })).ToArray();
            result.Add(new(alias, group.Key.Cohort.Kind.ToString(), group.Key.Cohort.AccountKey is not null,
                QuotaSnapshot.ClassifyAuthority(group.Key.Cohort.Source).ToString(), group.Key.HorizonHours, group.Count(), rows.Length,
                split, chosen, reasons, scores, policyScores, RollingScore("development-choice-policy", "final", finalPolicy),
                finalChosen.Count(t => t.Remaining is null && t.HeadRemaining is not null), includeTrials ? trials : []));
        }
        return new(RollingJointVersion, ForecastThresholdStudy.Fingerprint(data), availability, data.CapturedAtUtc,
            source.Quota.Count, quota.Length, quota.Count(q => q.HasSourceTimestamp && q.UsedPercent is { } u && double.IsFinite(u) && u is >= 0 and <= 100),
            built.Observations.Count, built.Coverage.Sum(c => c.CandidateStarts), result,
            "Explicit research. Nominal targets stay fixed; outcomes are poll-tolerant ordinary measurements, not latent exact costs. " +
            "Collection-qualified replay moves quota clocks to availability and retains original measurements; unknown/negative/>5m lag is a boundary. Reconstruction is separate. " +
            "First-20 fit is a comparator, not a sample-size theorem; expanding prefixes use only earlier matured and available same-cohort/horizon cost rows. Vocabulary/scaling are training-only. " +
            "At most 2048 intervals/cohort/horizon; truncation is explicit. Availability is conditional on constructed ordinary targets, not measured live abstention. " +
            "Last fifth of the cohort's retained timestamp span is final holdout; the split does not depend on quota values or target eligibility. Development labels must mature by split. The development choice is fixed before final outcomes; prefix fitting can learn from earlier matured final outcomes. " +
            "Points/bands and paired errors are separate. Residual bands use the existing completed-block owner; raw block-path P10/P90 is conditional spread, not predictive coverage. " +
            "Raw availability stays separate from candidate policies that retain the head on unavailable composition. Selection scores the full head-supported target set, requires 16 actual composed outcomes in eight blocks and 0.1pp/10% block-balanced improvement over both controls and a matched simpler parent policy. No automatic promotion/default change or account attribution.");
    }

    internal static (string Candidate, IReadOnlyList<string> Reasons) ChooseRollingDevelopment(IReadOnlyList<RollingJointTrial> trials)
    {
        var dev = trials.Where(t => t.Period == "development").ToArray();
        double margin = .1 * ForecastEvaluationOptions.Default.ImprovementMultiplier;
        var reasons = new List<string>(); string chosen = "released-head";
        var control = dev.Where(t => t.Candidate == "released-head" && t.Remaining is not null).ToArray();
        var eligible = new List<(string Candidate, double Loss)>();
        foreach (string candidate in RollingJointCandidates.Where(c => c is not "direct" and not "released-head"))
        {
            var raw = dev.Where(t => t.Candidate == candidate && t.Remaining is not null && t.HeadRemaining is not null).ToArray();
            var rawBlocks = ChronologicalEvidence.Blocks(raw, t => t.OriginUtc, t => t.OutcomeUtc, t => t.ResetUtc);
            var c = WithHeadFallback(dev, candidate).Where(t => t.Remaining is not null && t.HeadRemaining is not null).ToArray();
            var blocks = ChronologicalEvidence.Blocks(c, t => t.OriginUtc, t => t.OutcomeUtc, t => t.ResetUtc);
            if (c.Length != control.Length || rawBlocks.Sum(b => b.Count) < ForecastEvaluationOptions.Default.ComposedOutcomes ||
                rawBlocks.Count < ForecastEvaluationOptions.Default.CalibrationGroups) continue;
            double loss = blocks.Average(b => b.Average(t => t.IntervalLoss!.Value));
            double headLoss = blocks.Average(b => b.Average(t => EnvelopeLoss(t, t.HeadRemaining!.Value)));
            double directLoss = blocks.Average(b => b.Average(t => EnvelopeLoss(t, t.DirectRemaining)));
            if (loss + margin >= headLoss || loss >= headLoss * (1 - margin) || loss + margin >= directLoss || loss >= directLoss * (1 - margin) ||
                blocks.Count(b => b.Average(t => t.IntervalLoss!.Value) < b.Average(t => EnvelopeLoss(t, t.HeadRemaining!.Value))) <= blocks.Count / 2) continue;
            string? parent = candidate switch { "prefix-joint-2h" => "prefix-categories-2h", "prefix-joint-30m" => "prefix-categories-30m", "prefix-blocks-2h" => "prefix-joint-2h", _ => null };
            if (parent is not null)
            {
                var p = WithHeadFallback(dev, parent).Where(t => t.Remaining is not null).ToDictionary(t => t.OriginUtc);
                if (c.Any(t => !p.ContainsKey(t.OriginUtc))) continue;
                double parentLoss = blocks.Average(b => b.Average(t => p[t.OriginUtc].IntervalLoss!.Value));
                if (loss + margin >= parentLoss || loss >= parentLoss * (1 - margin) ||
                    blocks.Count(b => b.Average(t => t.IntervalLoss!.Value) < b.Average(t => p[t.OriginUtc].IntervalLoss!.Value)) <= blocks.Count / 2) continue;
            }
            eligible.Add((candidate, loss));
        }
        if (eligible.Count > 0) chosen = eligible.OrderBy(x => x.Loss).ThenBy(x => x.Candidate, StringComparer.Ordinal).First().Candidate;
        else reasons.Add("No development candidate supplied 16 composed outcomes/eight blocks and a material full-policy improvement over both controls (and its parent where applicable).");
        return (chosen, reasons);
    }

    private static double EnvelopeLoss(RollingJointTrial t, double remaining)
    {
        double delta = t.AnchorRemaining - remaining;
        return Math.Max(0, Math.Max(t.LowerDelta - delta, delta - t.UpperDelta));
    }

    private static RollingJointTrial[] WithHeadFallback(IReadOnlyList<RollingJointTrial> rows, string candidate)
    {
        var heads = rows.Where(t => t.Candidate == "released-head").ToDictionary(t => t.OriginUtc);
        return rows.Where(t => t.Candidate == candidate).Select(t =>
            t.Remaining is not null && heads.TryGetValue(t.OriginUtc, out var head) && head.Remaining is not null
                ? t : heads.TryGetValue(t.OriginUtc, out var fallback) ? fallback with { Candidate = candidate } : t).ToArray();
    }

    private static RollingJointScore RollingScore(string candidate, string period, RollingJointTrial[] rows)
    {
        var paired = rows.Where(t => t.Remaining is not null && t.HeadRemaining is not null).ToArray();
        var blocks = ChronologicalEvidence.Blocks(paired, t => t.OriginUtc, t => t.OutcomeUtc, t => t.ResetUtc);
        var band = paired.Where(t => t.LowerRemaining is not null && t.UpperRemaining is not null).ToArray();
        var paths = paired.Where(t => t.PathLowerRemaining is not null && t.PathUpperRemaining is not null).ToArray();
        double? Avg(IEnumerable<double> values) { var a = values.ToArray(); return a.Length == 0 ? null : a.Average(); }
        return new(candidate, period, rows.Length, rows.Count(t => t.Remaining is not null), paired.Length,
            rows.Count(t => t.LowerRemaining is not null && t.UpperRemaining is not null), band.Length, paths.Length, blocks.Sum(b => b.Count), blocks.Count,
            Avg(paired.Select(t => Math.Abs(t.Remaining!.Value - t.ObservedRemaining))), Avg(paired.Select(t => Math.Abs(t.HeadRemaining!.Value - t.ObservedRemaining))),
            Avg(paired.Select(t => t.IntervalLoss!.Value)), Avg(paired.Select(t => EnvelopeLoss(t, t.HeadRemaining!.Value))),
            blocks.Sum(b => b.Count) == 0 ? null : blocks.SelectMany(b => b).Sum(t => t.Remaining!.Value - t.ObservedRemaining),
            Avg(band.Select(t => t.ObservedRemaining >= t.LowerRemaining && t.ObservedRemaining <= t.UpperRemaining ? 1d : 0d)),
            Avg(band.Select(t => t.UpperRemaining!.Value - t.LowerRemaining!.Value)),
            Avg(paths.Select(t => t.ObservedRemaining >= t.PathLowerRemaining && t.ObservedRemaining <= t.PathUpperRemaining ? 1d : 0d)),
            Avg(paths.Select(t => t.PathUpperRemaining!.Value - t.PathLowerRemaining!.Value)),
            rows.Where(t => t.WithheldReason is not null).GroupBy(t => t.WithheldReason!).ToDictionary(g => g.Key, g => g.Count()));
    }
}
