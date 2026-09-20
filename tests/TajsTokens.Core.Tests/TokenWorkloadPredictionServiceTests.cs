// Taj's Tokens | TokenWorkloadPredictionServiceTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class TokenWorkloadPredictionServiceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    [Fact]
    public void RolloutTokensTrainWithoutAnyQuotaObservationsOrAccount()
    {
        CodexForecastDataset data = History(140);
        TokenWorkloadForecast forecast = TokenWorkloadPredictionService.Predict(data, data.Tokens[^1].ObservedAtUtc.AddMinutes(2));
        CodexForecastDataset withQuota = data with
        {
            Quota =
            [
                new QuotaSnapshot(
                    QuotaWindowKind.Weekly,
                    Start,
                    90,
                    10080,
                    Start.AddDays(7),
                    "codex",
                    "default",
                    "codex-app-server:codex"),
            ],
        };
        Assert.Equal(
            JsonSerializer.Serialize(forecast),
            JsonSerializer.Serialize(
                TokenWorkloadPredictionService.Predict(withQuota, data.Tokens[^1].ObservedAtUtc.AddMinutes(2))));
        Assert.Equal(140, forecast.TokenEvents);
        Assert.Equal(14, forecast.Sessions);
        Assert.Equal(2, forecast.Predictions.Count);
        Assert.All(forecast.Predictions, x => Assert.True(x.TrainingSamples >= 20));
        Assert.Contains("not proof", forecast.Methodology);
    }

    [Fact]
    public void NativeTokenModelLearnsHeldOutRelationshipAndBeatsPace()
    {
        CodexForecastDataset data = History(180);
        IReadOnlyList<TokenForecastTrial> trials = TokenWorkloadPredictionService.Replay(data, 0.5);
        Assert.Contains(trials, x => x.Prediction.Model == "workload-ridge");
        IReadOnlyList<TokenForecastScore> scores = TokenWorkloadPredictionService.EvaluateScores(data);
        TokenForecastScore baseline = scores.Single(x => x.HorizonHours == 0.5 && x.Model == "recent-30m");
        TokenForecastScore selected = scores.Single(x => x.HorizonHours == 0.5 && x.Model == TokenWorkloadPredictionService.PolicyVersion);
        Assert.True(selected.MeanAbsoluteError < baseline.MeanAbsoluteError);
    }

    [Fact]
    public void FutureTokensAndFutureModelMetadataDoNotChangeEarlierPredictions()
    {
        CodexForecastDataset data = History(90);
        IReadOnlyList<TokenForecastTrial> before = TokenWorkloadPredictionService.Replay(data, 0.5);
        CodexForecastDataset future = History(130);
        IReadOnlyList<TokenForecastTrial> after = TokenWorkloadPredictionService.Replay(future, 0.5);
        foreach (TokenForecastTrial trial in before)
        {
            TokenForecastTrial matching = after.Single(x => x.OriginUtc == trial.OriginUtc);
            Assert.Equal(JsonSerializer.Serialize(trial), JsonSerializer.Serialize(matching));
        }
        DateTimeOffset now = Start.AddHours(43);
        Assert.Equal(
            JsonSerializer.Serialize(TokenWorkloadPredictionService.Predict(data, now)),
            JsonSerializer.Serialize(TokenWorkloadPredictionService.Predict(future, now)));
    }

    [Fact]
    public void BackfilledRolloutsAreUsableForRetrospectiveTrainingButNotBeforeCollection()
    {
        CodexForecastDataset data = History(100);
        DateTimeOffset now = data.Tokens[^1].ObservedAtUtc.AddMinutes(2);
        CodexForecastDataset backfilled = data with { Tokens = data.Tokens.Select(x => x with { CapturedAtUtc = now }).ToArray() };
        TokenWorkloadForecast prediction = TokenWorkloadPredictionService.Predict(backfilled, now);
        Assert.True(prediction.Predictions[0].TrainingSamples >= 20);
        Assert.Empty(TokenWorkloadPredictionService.Predict(backfilled, now.AddMinutes(-1)).Predictions);
    }

    [Fact]
    public void SparseOrIdleInputsDoNotClaimACalibratedRange()
    {
        CodexForecastDataset data = History(8);
        DateTimeOffset now = data.Tokens[^1].ObservedAtUtc;
        Assert.All(TokenWorkloadPredictionService.Predict(data, now).Predictions, x => Assert.Null(x.LowerTokens));
        Assert.Empty(TokenWorkloadPredictionService.Predict(data, now.AddHours(3)).Predictions);
        Assert.Empty(TokenWorkloadPredictionService.Predict(data with { Tokens = [] }, now).Predictions);
    }

    [Fact]
    public void SubTickHorizonIsRejectedBeforeGridArithmetic()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenWorkloadPredictionService.Replay(History(8), double.Epsilon));
    }

    [Fact]
    public void NowcastUsesShortHorizonsAndPausesAtTenMinutesWithoutClaimingNoSession()
    {
        CodexForecastDataset data = History(40);
        DateTimeOffset latest = data.Tokens[^1].ObservedAtUtc;
        TokenWorkloadForecast active = TokenWorkloadPredictionService.Predict(data, latest.AddMinutes(9));
        Assert.Equal(new[] { 5d / 60, .25 }, active.Predictions.Select(x => x.HorizonHours));
        TokenWorkloadForecast quiet = TokenWorkloadPredictionService.Predict(data, latest.AddMinutes(10));
        Assert.Empty(quiet.Predictions);
        Assert.Equal(NowcastActivityState.NoRecentActivity, quiet.Activity!.State);
        Assert.Contains("not proof", quiet.Activity.Explanation);
    }

    [Fact]
    public void QuietOpenTurnsAndLateOrFutureEvidenceAreNotLivenessProof()
    {
        CodexForecastDataset data = History(40);
        DateTimeOffset now = data.Tokens[^1].ObservedAtUtc.AddMinutes(20);

        CodexWorkloadObservation Event(string type, DateTimeOffset at, DateTimeOffset captured)
        {
            return new CodexWorkloadObservation(
                type,
                "source",
                "file",
                0,
                1,
                "session",
                type,
                at,
                captured,
                "turn",
                null,
                null,
                null,
                null,
                null);
        }

        data = data with { Workload = [Event("task_started", now.AddMinutes(-30), now.AddMinutes(-30))] };
        NowcastActivity quiet = CodexNowcastActivity.Evaluate(data, now);
        Assert.Equal(NowcastActivityState.QuietOpenTurn, quiet.State);
        Assert.False(quiet.SupportsNowcast);
        CodexForecastDataset later = data with
        {
            Workload =
            [
                .. data.Workload,
                Event("task_complete", now.AddMinutes(1), now.AddMinutes(1)),
                Event("function_call", now.AddMinutes(-1), now.AddMinutes(1)),
            ],
        };
        Assert.Equal(quiet, CodexNowcastActivity.Evaluate(later, now, ForecastReplayAvailability.CollectedByOrigin));
        Assert.True(CodexNowcastActivity.Evaluate(later, now).SupportsNowcast);
        CodexForecastDataset staleTokens = data with
        {
            Tokens = data.Tokens.Select(x => x with { ObservedAtUtc = x.ObservedAtUtc.AddHours(-3) }).ToArray(),
            Workload = [Event("function_call", now.AddMinutes(-1), now)],
        };
        TokenWorkloadForecast unanchored = TokenWorkloadPredictionService.Predict(staleTokens, now);
        Assert.True(unanchored.Activity!.SupportsNowcast);
        Assert.Empty(unanchored.Predictions);
        Assert.Contains("Insufficient recent token history", unanchored.Activity.Explanation);
        Assert.Equal(
            NowcastActivityState.Unknown,
            CodexNowcastActivity.Evaluate(
                data with { Workload = [], Tokens = data.Tokens.Select(x => x with { CapturedAtUtc = null }).ToArray() },
                now,
                ForecastReplayAvailability.CollectedByOrigin).State);
    }

    [Fact]
    public void ShortReplayUsesTheSameActivityGateAndFutureDataCannotChangeItsPrefix()
    {
        CodexForecastDataset data = History(40);
        IReadOnlyList<TokenForecastTrial> before = TokenWorkloadPredictionService.Replay(data, .25);
        IReadOnlyList<TokenForecastTrial> after = TokenWorkloadPredictionService.Replay(History(50), .25);
        Assert.NotEmpty(before);
        foreach (TokenForecastTrial trial in before)
        {
            Assert.True(CodexNowcastActivity.Evaluate(data, trial.OriginUtc).SupportsNowcast);
            Assert.Equal(JsonSerializer.Serialize(trial), JsonSerializer.Serialize(after.Single(x => x.OriginUtc == trial.OriginUtc)));
        }
    }

    private static CodexForecastDataset History(int count)
    {
        CodexPredictiveTokenEvent[] tokens = Enumerable.Range(0, count).Select(i =>
        {
            DateTimeOffset at = Start.AddMinutes(i * 30 + 10);
            bool high = i % 2 == 0;
            return new CodexPredictiveTokenEvent(
                $"session-{i / 10}",
                at,
                at,
                i >= 100 ? "new-model" : "model-a",
                high ? "high" : "low",
                high ? 10000 : 100,
                0,
                0,
                0,
                0,
                high ? 10000 : 100);
        }).ToArray();
        return new CodexForecastDataset([], [], tokens, [], tokens[^1].ObservedAtUtc, "Synthetic token pattern, not empirical performance");
    }
}