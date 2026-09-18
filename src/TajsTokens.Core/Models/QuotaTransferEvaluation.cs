namespace TajsTokens.Core.Models;

public sealed record QuotaTransferScore(QuotaHistoryCohort Source, QuotaHistoryCohort Destination,
    double HorizonHours, string Model, int SourceTraining, int DestinationTraining, int HeldOut,
    int HeldOutGenerations, double Scale, double? UnscaledLoss, double? ScaledLoss, double? LocalOnlyLoss,
    bool MaterialImprovement, string Status);

public sealed record QuotaTransferEvaluation(string Version, string Methodology, IReadOnlyList<QuotaTransferScore> Scores);
