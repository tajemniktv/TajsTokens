using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Research;

public sealed record AlignedCostRow(DateTimeOffset OriginUtc, DateTimeOffset OutcomeUtc, DateTimeOffset FiveResetUtc,
    DateTimeOffset WeeklyResetUtc, string Period, long Tokens, IReadOnlyList<double> Categories, CodexJointWorkload? Joint,
    double FiveObserved, double FiveLower, double FiveUpper, double WeeklyObserved, double WeeklyLower, double WeeklyUpper,
    double FiveSeparate, double WeeklySeparate, double? FiveShared, double? WeeklyShared);
public sealed record AlignedCostTraining(DateTimeOffset OriginUtc, DateTimeOffset OutcomeUtc, long Tokens,
    IReadOnlyList<double> Categories, CodexJointWorkload? Joint, double FiveLower, double FiveUpper, double WeeklyLower, double WeeklyUpper);
public sealed record AlignedCostScore(string Window, string Period, int Origins, int SharedAvailable, int Matched,
    int NonOverlapping, int Blocks, double? SeparateMae, double? SharedMae, double? SeparateBlockLoss,
    double? SharedBlockLoss, double? ZeroBlockLoss, double? SharedSignedSum);
public sealed record AlignedCostModel(string Model, string TrainingPolicy, string Status, int TrainingRows, DateTimeOffset? TrainingThroughUtc,
    DateTimeOffset? TrainingAvailableUtc, double? FiveToWeeklyScale, double? WeeklyToFiveScale,
    IReadOnlyList<string> Qualifications, IReadOnlyList<AlignedCostTraining> Training,
    IReadOnlyList<AlignedCostScore> Scores, IReadOnlyList<AlignedCostRow> Trials);
public sealed record AlignedWindowGroup(string Cohort, double HorizonHours, int? FiveHourMinutes, int? WeeklyMinutes,
    int AlignedIntervals, int ExaminedIntervals, int PositiveWorkIntervals, int FinalAlignedIntervals, int PositiveFinalIntervals,
    IReadOnlyList<AlignedCostTraining> Frame, IReadOnlyList<AlignedCostModel> Models);
public sealed record AlignedWindowComparison(string Version, DateTimeOffset FromUtc, DateTimeOffset ThroughUtc,
    DateTimeOffset SplitUtc, ForecastReplayAvailability Availability, int FiveHourIntervals, int AlignedIntervals,
    IReadOnlyDictionary<string, int> Unpaired, IReadOnlyList<AlignedWindowGroup> Groups, string Boundary);

public static partial class QuotaTransferEvaluator
{
    public const string AlignedVersion = "aligned-window-cost/v1";
    private sealed record Pair(QuotaCostObservation Five, QuotaCostObservation Week);

    public static AlignedWindowComparison EvaluateAligned(IReadOnlyList<QuotaCostObservation> observations,
        DateTimeOffset from, DateTimeOffset through, ForecastReplayAvailability availability,
        CancellationToken token = default, bool includeTrials = false)
    {
        if (from >= through || through - from > TimeSpan.FromDays(366) || !Enum.IsDefined(availability))
            throw new ArgumentException("Choose a chronological bounded range and known replay mode.");
        long span = (through - from).Ticks; var split = from.AddTicks(span / 5 * 4 + span % 5 * 4 / 5);
        bool strict = availability == ForecastReplayAvailability.CollectedByOrigin;
        var rows = observations.Where(r => r.StartUtc >= from && r.EndUtc <= through &&
            QuotaSnapshot.ClassifyAuthority(r.Cohort.Source) == QuotaObservationAuthority.ProviderAuthoritative).ToArray();
        (QuotaHistoryCohort, DateTimeOffset, DateTimeOffset, double) Key(QuotaCostObservation r) =>
            (r.Cohort with { Kind = QuotaWindowKind.Unknown, WindowMinutes = null }, r.StartUtc, r.EndUtc, r.HorizonHours);
        var five = rows.Where(r => r.Cohort.Kind == QuotaWindowKind.FiveHour).GroupBy(Key).ToArray();
        var weekly = rows.Where(r => r.Cohort.Kind == QuotaWindowKind.Weekly).ToLookup(Key);
        var pairs = new List<Pair>(); var rejected = new Dictionary<string, int>();
        void Reject(string reason, int count) => rejected[reason] = rejected.GetValueOrDefault(reason) + count;
        foreach (var group in five)
        {
            token.ThrowIfCancellationRequested(); var destination = weekly[group.Key].ToArray();
            if (string.IsNullOrWhiteSpace(group.Key.Item1.AccountKey)) { Reject("unknown-account", group.Count()); continue; }
            if (group.Count() != 1 || destination.Length != 1) { Reject("missing-or-ambiguous-aligned-weekly-interval", group.Count()); continue; }
            var a = group.Single(); var b = destination[0];
            if (a.Features.Tokens != b.Features.Tokens || !a.TokenCategories.SequenceEqual(b.TokenCategories) ||
                !Equals(a.Features.JointWorkload, b.Features.JointWorkload)) { Reject("different-recorded-workload", 1); continue; }
            if (strict && new[] { a, b }.Any(r => r.TokenCostEvidenceAvailableAtUtc is null ||
                r.TokenCostEvidenceAvailableAtUtc > r.EndUtc.AddMinutes(5) || r.TokenCostEvidenceAvailableAtUtc > through))
            { Reject("label-unavailable-or-late", 1); continue; }
            pairs.Add(new(a, b));
        }
        var groups = new List<AlignedWindowGroup>();
        foreach (var group in pairs.GroupBy(p => (p.Five.Cohort, Week: p.Week.Cohort, p.Five.HorizonHours)))
        {
            var ordered = group.OrderBy(p => p.Five.StartUtc).Take(QuotaCostIdentification.MaximumRows).ToArray();
            var models = new List<AlignedCostModel>();
            foreach (string policy in new[] { "first20", "development-prefix" })
            foreach (string model in new[] { "total", "categories", "joint-categories" })
            {
                token.ThrowIfCancellationRequested();
                var development = ordered.Where(p => p.Five.EndUtc <= split);
                var training = (policy == "first20" ? development.Take(20) : development).ToArray();
                DateTimeOffset? available = training.Length == 0 || training.Any(p => p.Five.TokenCostEvidenceAvailableAtUtc is null || p.Week.TokenCostEvidenceAvailableAtUtc is null)
                    ? null : training.SelectMany(p => new[] { p.Five.TokenCostEvidenceAvailableAtUtc, p.Week.TokenCostEvidenceAvailableAtUtc }).Max();
                string? issue = training.Length < 20 ? "insufficient-aligned-development-training" :
                    strict && (available is null || available > split) ? "training-unavailable-by-development-cutoff" :
                    !QuotaAccountingModel.HasTrainingWork(training.Select(p => p.Five).ToArray()) ? "no-recorded-training-work" :
                    model != "total" && training.Any(p => !p.Five.HasCompleteTokenCategories || !p.Week.HasCompleteTokenCategories) ? "incomplete-category-training" :
                    model == "joint-categories" && training.Any(p => !QuotaCostIdentification.Valid(p.Five) || !QuotaCostIdentification.Valid(p.Week)) ? "incomplete-joint-training" : null;
                if (issue is not null)
                { models.Add(new(model, policy, issue, training.Length, training.LastOrDefault()?.Five.EndUtc, available, null, null, [],
                    includeTrials ? training.Select(TrainingRow).ToArray() : [], [], [])); continue; }
                var fitFive = QuotaAccountingModel.FitFrozen(training.Select(p => p.Five).ToArray(), model, token);
                var fitWeek = QuotaAccountingModel.FitFrozen(training.Select(p => p.Week).ToArray(), model, token);
                var fivePred = training.Select(p => fitFive(p.Five)).ToArray(); var weekPred = training.Select(p => fitWeek(p.Week)).ToArray();
                double? fiveToWeek = fivePred.Average(x => x * x) <= 1e-12 ? null : FitScale(fivePred, training.Select(p => p.Week).ToArray());
                double? weekToFive = weekPred.Average(x => x * x) <= 1e-12 ? null : FitScale(weekPred, training.Select(p => p.Five).ToArray());
                var qualifications = new List<string>();
                if (fiveToWeek is null) qualifications.Add("five-hour-reference-has-no-scale-support");
                if (weekToFive is null) qualifications.Add("weekly-reference-has-no-scale-support");
                if (training.Any(p => p.Five.QualityFlags.Contains("meter-precision-unverified-sensitivity-only") || p.Week.QualityFlags.Contains("meter-precision-unverified-sensitivity-only")))
                    qualifications.Add("unverified-meter-precision");
                var fixedAt = policy == "development-prefix" ? split : training[^1].Five.EndUtc;
                var trials = ordered.Where(p => p.Five.StartUtc >= fixedAt &&
                    (!strict || p.Five.StartUtc >= available)).Where(p => model == "total" ||
                    p.Five.HasCompleteTokenCategories && p.Week.HasCompleteTokenCategories && (model != "joint-categories" || QuotaCostIdentification.Valid(p.Five)))
                    .Select(p => new AlignedCostRow(p.Five.StartUtc, p.Five.EndUtc, p.Five.ResetUtc, p.Week.ResetUtc,
                        p.Five.StartUtc >= split ? "final" : p.Five.EndUtc <= split ? "development" : "boundary",
                        p.Five.Features.Tokens, p.Five.TokenCategories, p.Five.Features.JointWorkload,
                        p.Five.ObservedDelta, p.Five.LowerDelta, p.Five.UpperDelta, p.Week.ObservedDelta, p.Week.LowerDelta, p.Week.UpperDelta,
                        fitFive(p.Five), fitWeek(p.Week), weekToFive * fitWeek(p.Week), fiveToWeek * fitFive(p.Five))).ToArray();
                var scores = new[] { "development", "final" }.SelectMany(period => new[] { false, true }.Select(week =>
                    ScoreAligned(trials.Where(t => t.Period == period).ToArray(), week, period))).ToArray();
                if (!trials.Any(t => t.Period == "final")) qualifications.Add("no-final-aligned-outcomes");
                else if (!trials.Any(t => t.Period == "final" && t.Tokens > 0)) qualifications.Add("no-positive-recorded-work-final");
                models.Add(new(model, policy, "research-only", training.Length, training[^1].Five.EndUtc, available, fiveToWeek, weekToFive, qualifications,
                    includeTrials ? training.Select(TrainingRow).ToArray() : [], scores, includeTrials ? trials : []));
            }
            groups.Add(new("cohort-" + (groups.Count + 1), group.Key.HorizonHours, group.Key.Cohort.WindowMinutes,
                group.Key.Week.WindowMinutes, group.Count(), ordered.Length, ordered.Count(p => p.Five.Features.Tokens > 0),
                ordered.Count(p => p.Five.StartUtc >= split), ordered.Count(p => p.Five.StartUtc >= split && p.Five.Features.Tokens > 0),
                includeTrials ? ordered.Select(TrainingRow).ToArray() : [], models));
        }
        return new(AlignedVersion, from, through, split, availability, five.Sum(g => g.Count()), pairs.Count, rejected, groups,
            "Actual interval workload is an oracle cost input, not an end-to-end forecast. Pair exact origins/endpoints/horizons and matching reported account/source/profile/plan/bucket; slots are not semantic identities. " +
            "Separate maps and two directional proportional controls share the same aligned training set. First-20 is retained as a control, not a sample-size theorem; development-prefix fits all development rows and freezes at the split, never scoring earlier origins with future training. Reuse the existing frozen nonnegative fits and scalar interval-distance solver; flat source references withhold scale rather than infer free work. " +
            "Last fifth of the declared timestamp range is final holdout; training must mature by its boundary. Collection qualification and category/joint availability remain explicit. " +
            "At most 2048 aligned rows/group; source/examined counts expose truncation. Paired errors, non-overlap and blocks are separate; block boundaries stop at either window reset. " +
            "These directional references do not optimize or prove/reject every rank-one cost model. No causal policy conclusion, universal TT scale, probability, automatic transfer or live selection follows.");
    }

    private static AlignedCostTraining TrainingRow(Pair p) => new(p.Five.StartUtc, p.Five.EndUtc, p.Five.Features.Tokens,
        p.Five.TokenCategories, p.Five.Features.JointWorkload, p.Five.LowerDelta, p.Five.UpperDelta, p.Week.LowerDelta, p.Week.UpperDelta);

    private static AlignedCostScore ScoreAligned(AlignedCostRow[] rows, bool week, string period)
    {
        double? Shared(AlignedCostRow r) => week ? r.WeeklyShared : r.FiveShared;
        double Separate(AlignedCostRow r) => week ? r.WeeklySeparate : r.FiveSeparate;
        double Observed(AlignedCostRow r) => week ? r.WeeklyObserved : r.FiveObserved;
        double Loss(AlignedCostRow r, double value) => Math.Max(0, Math.Max((week ? r.WeeklyLower : r.FiveLower) - value, value - (week ? r.WeeklyUpper : r.FiveUpper)));
        var paired = rows.Where(r => Shared(r) is not null).ToArray();
        var blocks = ChronologicalEvidence.Blocks(paired, r => r.OriginUtc, r => r.OutcomeUtc,
            r => r.FiveResetUtc < r.WeeklyResetUtc ? r.FiveResetUtc : r.WeeklyResetUtc);
        double? Mean(IEnumerable<double> v) { var a = v.ToArray(); return a.Length == 0 ? null : a.Average(); }
        return new(week ? "Weekly" : "FiveHour", period, rows.Length, paired.Length, paired.Length, blocks.Sum(b => b.Count), blocks.Count,
            Mean(paired.Select(r => Math.Abs(Separate(r) - Observed(r)))), Mean(paired.Select(r => Math.Abs(Shared(r)!.Value - Observed(r)))),
            Mean(blocks.Select(b => b.Average(r => Loss(r, Separate(r))))), Mean(blocks.Select(b => b.Average(r => Loss(r, Shared(r)!.Value)))),
            Mean(blocks.Select(b => b.Average(r => Loss(r, 0)))), paired.Length == 0 ? null : blocks.SelectMany(b => b).Sum(r => Shared(r)!.Value - Observed(r)));
    }
}
