using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Research;

/// <summary>Explicit cost-only comparison; live accounting does not enumerate these ablations.</summary>
public static class QuotaCostEvaluation
{
    public const string Version = QuotaAccountingModel.Version;
    public const string Methodology = QuotaAccountingModel.Methodology;
    public static readonly string[] Candidates = ["persistence", "pace", "total", "categories", "model-effort",
        "context-ablation", "activity-ablation", "runtime-ablation", "time-ablation"];

    public static QuotaCostReport Evaluate(CodexForecastDataset data, CancellationToken cancellationToken = default,
        bool userConfirmedRolloutOwnership = false, IReadOnlyList<double>? horizons = null)
    {
        var built = QuotaCostObservationBuilder.BuildDetailed(data, cancellationToken, userConfirmedRolloutOwnership, horizons);
        return Evaluate(built.Observations, (userConfirmedRolloutOwnership ?
            "User confirms retained rollouts belong to their account. Native account IDs remain absent; sessions/sources are not pooled. " : "") + data.Coverage + " " +
            QuotaHistoryPolicy.Summarize(QuotaHistoryPolicy.Describe(data.Quota, data.CapturedAtUtc)), cancellationToken)
            with { DatasetCapturedAtUtc = data.CapturedAtUtc, EvidenceCoverage = QuotaEvaluationCoverageBuilder.Build(data),
                ConstructionCoverage = built.Coverage };
    }

    public static QuotaCostReport Evaluate(IReadOnlyList<QuotaCostObservation> rows, string coverage,
        CancellationToken cancellationToken = default) => QuotaAccountingModel.Evaluate(rows, coverage, cancellationToken,
            rows.Any(x => x.ApiPriceWeight is not null) ? Candidates.Append("api-price").ToArray() : Candidates, QuotaCostAblations.For);
}
