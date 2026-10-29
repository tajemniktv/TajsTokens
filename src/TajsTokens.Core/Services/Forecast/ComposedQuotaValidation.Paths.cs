using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public static partial class ComposedQuotaValidation
{
    internal static PredictedWorkload DescribeJoint(CodexJointWorkload joint, WorkloadContinuationReceipt receipt)
    {
        double[] categories = Enumerable.Range(0, 5).Select(c => (double)(joint.Cells.Sum(x => TtJointWorkloadBasis.Amount(x, (TtTokenCategory)c)) +
            (joint.Overflow is { } overflow ? TtJointWorkloadBasis.Amount(overflow, (TtTokenCategory)c) : 0))).ToArray();
        IReadOnlyDictionary<string, double> Shares(Func<CodexWorkloadCoordinate, string?> field) => joint.Cells
            .Where(x => !string.IsNullOrWhiteSpace(field(x.Coordinate!))).GroupBy(x => field(x.Coordinate!)!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => joint.ReportedTokens > 0 ? (double)(x.Sum(c => c.ReportedTotal) / joint.ReportedTokens) : 0);
        return new(receipt.ObservationToUtc, receipt.HorizonHours, (double)joint.ReportedTokens, categories,
            Shares(x => x.Model), Shares(x => x.ReasoningEffort), receipt.Observations, receipt.LatestEvidenceUtc ?? receipt.ObservationToUtc,
            receipt.Availability, receipt.Methodology) { JointWorkload = joint, Continuation = receipt };
    }

    internal sealed class JointPathInputCheck
    {
        private readonly QuotaCostObservation[] _training;
        private readonly HashSet<(string?, int)> _coordinates;
        private readonly bool _unknownTraining;
        private readonly double _violation;
        public JointPathInputCheck(QuotaCostObservation[] training, Func<QuotaCostObservation, double> fit)
        {
            _training = training;
            _violation = training.Max(x => x.IntervalLoss(fit(x)));
            var models = training.SelectMany(x => x.Features.JointWorkload!.Cells).Select(x => x.Coordinate!.Model)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(16).ToHashSet(StringComparer.Ordinal);
            _unknownTraining = training.Any(x => x.Features.JointWorkload!.MissingModelTokens > 0 ||
                (x.Features.JointWorkload.Overflow?.ReportedTotal ?? 0) > 0 || x.Features.JointWorkload.Cells.Any(c => c.ReportedTotal > 0 && !models.Contains(c.Coordinate!.Model)));
            _coordinates = training.SelectMany(x => x.Features.JointWorkload!.Cells).Where(x => models.Contains(x.Coordinate!.Model))
                .SelectMany(x => Enumerable.Range(0, 5).Where(c => TtJointWorkloadBasis.Amount(x, (TtTokenCategory)c) > 0).Select(c => (x.Coordinate!.Model, c))).ToHashSet();
        }

        public JointQuotaPathEvidence Describe(CodexJointWorkload projected, double context, JointWorkloadPaths? ensemble,
            Func<CodexJointWorkload, double> cost)
        {
            bool unknown = projected.MissingModelTokens > 0 || (projected.Overflow?.ReportedTotal ?? 0) > 0;
            int novel = projected.Cells.SelectMany(x => Enumerable.Range(0, 5)
                    .Where(c => TtJointWorkloadBasis.Amount(x, (TtTokenCategory)c) > 0).Select(c => (x.Coordinate!.Model, c)))
                .Distinct().Count(x => !_coordinates.Contains(x));
            var reasons = new List<string>();
            if (_unknownTraining) reasons.Add("unknown-or-overflow-training-coordinates");
            if (_violation > QuotaScenarioSupport.NumericalTolerance) reasons.Add("frozen-fit-outside-declared-measurement-envelopes");
            if (unknown) reasons.Add("unknown-or-overflow-projected-coordinates");
            if (novel > 0) reasons.Add("novel-projected-coordinates");
            double[] amounts = ensemble?.Paths.Select(x => cost(x.Aggregate)).Order().ToArray() ?? [];
            double? Quantile(double q) => amounts.Length == 0 ? null : amounts[Math.Clamp((int)Math.Ceiling(amounts.Length * q) - 1, 0, amounts.Length - 1)];
            return new(ensemble?.Version ?? WorkloadCompositionPrediction.ContinuationVersion, context, _training[0].StartUtc, _training[^1].EndUtc, _violation,
                Array.AsReadOnly(reasons.ToArray()), novel, unknown,
                projected.ReportedTokens < (decimal)_training.Min(x => x.Features.Tokens) || projected.ReportedTokens > (decimal)_training.Max(x => x.Features.Tokens),
                ensemble?.Paths.Count ?? 1, ensemble?.Seed, ensemble?.InputRevision, Quantile(.1), Quantile(.9));
        }
    }
}
