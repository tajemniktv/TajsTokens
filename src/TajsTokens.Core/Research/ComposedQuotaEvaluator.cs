// Taj's Tokens | ComposedQuotaEvaluator.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Research;

public static class ComposedQuotaEvaluator
{
    public const string Version = ComposedQuotaValidation.Version;
    public static readonly double[] EvaluationHorizons = [5d / 60, .25, .5, 2];

    public static ComposedQuotaEvaluation Evaluate(
        CodexForecastDataset data,
        CancellationToken cancellationToken = default,
        ForecastReplayAvailability availability = ForecastReplayAvailability.ReconstructedEventTime,
        IReadOnlyList<double>? horizons = null)
    {
        return ComposedQuotaValidation.Evaluate(data, cancellationToken, availability, horizons ?? EvaluationHorizons);
    }
}