// Taj's Tokens | QuotaCostEvaluation.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Research;

/// <summary>Explicit cost-only comparison; live accounting does not enumerate these ablations.</summary>
public static class QuotaCostEvaluation
{
    public const string Version = "quota-cost-research/v8-joint-shrink";
    public const string Methodology = QuotaAccountingModel.Methodology +
        " Joint-categories is an explicit research challenger: direct model/category cells, no effort/tier multiplier, " +
        "nonnegative interval-distance ridge with penalty 0.1 toward training-only pooled category coefficients in RMS-scaled coordinates. " +
        "Missing model and vocabulary overflow remain separate bins. Unseen coordinates inherit a regularization assumption, not identified prices. " +
        "Matched pace/total/category losses are descriptive; the joint candidate cannot earn a promotion label. " +
        "Zero local work against positive quota bounds remains unexplained, not an intercept or inferred external usage.";

    public static readonly string[] Candidates =
    [
        "persistence", "pace", "total", "categories", "model-effort", "joint-categories",
        "context-ablation", "activity-ablation", "runtime-ablation", "time-ablation",
    ];

    public static QuotaCostReport Evaluate(
        CodexForecastDataset data,
        CancellationToken cancellationToken = default,
        bool userConfirmedRolloutOwnership = false,
        IReadOnlyList<double>? horizons = null)
    {
        QuotaCostObservationBuild built = QuotaCostObservationBuilder.BuildDetailed(
            data,
            cancellationToken,
            userConfirmedRolloutOwnership,
            horizons);
        return Evaluate(
                built.Observations,
                (userConfirmedRolloutOwnership
                    ? "User confirms retained rollouts belong to their account. Native account IDs remain absent; sessions/sources are not pooled. "
                    : "") + data.Coverage + " " +
                QuotaHistoryPolicy.Summarize(QuotaHistoryPolicy.Describe(data.Quota, data.CapturedAtUtc)),
                cancellationToken)
            with
            {
                DatasetCapturedAtUtc = data.CapturedAtUtc,
                EvidenceCoverage = QuotaEvaluationCoverageBuilder.Build(data),
                ConstructionCoverage = built.Coverage,
                Identification = QuotaCostIdentification.Analyze(built.Observations, cancellationToken),
            };
    }

    public static QuotaCostReport Evaluate(
        IReadOnlyList<QuotaCostObservation> rows,
        string coverage,
        CancellationToken cancellationToken = default)
    {
        return QuotaAccountingModel.Evaluate(
            rows,
            coverage,
            cancellationToken,
            rows.Any(x => x.ApiPriceWeight is not null) ? Candidates.Append("api-price").ToArray() : Candidates,
            QuotaCostAblations.For) with { Version = Version, Methodology = Methodology };
    }
}
