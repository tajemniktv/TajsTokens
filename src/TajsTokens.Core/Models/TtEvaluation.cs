namespace TajsTokens.Core.Models;

public sealed record TtEvaluationScore(QuotaHistoryCohort Cohort, double HorizonHours, TtWorkloadBasis? Basis,
    string Status, int BasisIntervals, int CalibrationIntervals, int HeldOutIntervals, int UnsupportedIntervals,
    double UnsupportedTokens, int ResetGenerations, double? QuotaPointsPerTt, double? HeldOutTt,
    double? ScalarLoss, double? FullVectorLoss, double? RawTokenLoss)
{
    public QuotaHistoryCohort? BasisCohort { get; init; }
    public bool IsTransfer { get; init; }
    public DateTimeOffset? BasisEndUtc { get; init; }
    public DateTimeOffset? CalibrationEndUtc { get; init; }
    public double? ScalarMae { get; init; }
    public double? FullVectorMae { get; init; }
    public double? RawTokenMae { get; init; }
    public double? ZeroLoss { get; init; }
    public double? ScalarBias { get; init; }
    public double? CumulativeError { get; init; }
    public double? CumulativeErrorLower { get; init; }
    public double? CumulativeErrorUpper { get; init; }
}

public sealed record TtEvaluation(string Version, string Methodology, IReadOnlyList<TtEvaluationScore> Scores);
