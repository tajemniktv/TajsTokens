// Taj's Tokens | WorkloadCompositionPredictionTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class WorkloadCompositionPredictionTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-09-18T10:00:00Z");
    private static readonly TokenHorizonPrediction Prediction = new(0.5, 1000, "fixture", 20, 16, 1, null, null, "fixture");

    [Fact]
    public void OpposingCategoryErrorsCannotCancelIntoApparentlyCompleteComposition()
    {
        var missing = new CodexPredictiveTokenEvent("s", Origin.AddMinutes(-1), Origin, "m", "high", 0, 0, 0, 0, 0, 100);
        CodexPredictiveTokenEvent excess = missing with { UncachedInputTokens = 200 };
        var data = new CodexForecastDataset([], [], [missing, excess], [], Origin, "fixture");
        Assert.Null(WorkloadCompositionPrediction.Project(data, Origin, Prediction));
        Assert.Equal(200, data.Tokens.Sum(x => x.ReportedTotalTokens));
    }

    [Fact]
    public void CompositionConservesPredictionAndNeverSeesFutureModelOrTokens()
    {
        var past = new CodexPredictiveTokenEvent("s", Origin.AddMinutes(-1), Origin, "old", null, 10, 70, 0, 15, 5, 100);
        CodexPredictiveTokenEvent future = past with
        {
            ObservedAtUtc = Origin.AddSeconds(1), Model = "future", UncachedInputTokens = 99999,
        };
        var data = new CodexForecastDataset([], [], [past, future], [], Origin.AddDays(1), "fixture");
        PredictedWorkload result = WorkloadCompositionPrediction.Project(data, Origin, Prediction)!;
        Assert.Equal(new double[] { 100, 700, 0, 150, 50 }, result.TokenCategories);
        Assert.Equal(1000, result.TokenCategories.Sum());
        Assert.Single(result.ModelShares);
        Assert.Empty(result.EffortShares);
        Assert.Equal(1, result.CompositionObservations);
    }

    [Fact]
    public void MissingBackfilledOrOldCompositionIsNotInventedAsZero()
    {
        var row = new CodexPredictiveTokenEvent("s", Origin.AddMinutes(-1), Origin.AddDays(1), "old", "high", 10, 0, 0, 0, 0, 10);
        var data = new CodexForecastDataset([], [], [row], [], Origin.AddDays(1), "fixture");
        Assert.Null(WorkloadCompositionPrediction.Project(data, Origin, Prediction, ForecastReplayAvailability.CollectedByOrigin));
        Assert.NotNull(WorkloadCompositionPrediction.Project(data, Origin, Prediction));
        Assert.Null(WorkloadCompositionPrediction.Project(data, Origin.AddHours(3), Prediction));
    }

    [Fact]
    public void HistoricalTokenForecastSeparatesEventTimeFromCollectionTime()
    {
        CodexPredictiveTokenEvent[] rows = Enumerable.Range(0, 20).Select(i => new CodexPredictiveTokenEvent(
            "s",
            Origin.AddMinutes(-300 + i * 15),
            Origin.AddDays(1),
            "model",
            "high",
            100,
            0,
            0,
            0,
            0,
            100)).ToArray();
        var data = new CodexForecastDataset([], [], rows, [], Origin.AddDays(1), "fixture");
        Assert.NotNull(TokenWorkloadPredictionService.PredictHorizon(data, Origin, 0.5)?.Composition);
        Assert.Null(TokenWorkloadPredictionService.PredictHorizon(data, Origin, 0.5, ForecastReplayAvailability.CollectedByOrigin));
    }
}