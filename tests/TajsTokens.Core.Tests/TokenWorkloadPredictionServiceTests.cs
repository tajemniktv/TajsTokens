using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class TokenWorkloadPredictionServiceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    [Fact]
    public void RolloutTokensTrainWithoutAnyQuotaObservationsOrAccount()
    {
        var data = History(140);
        var forecast = TokenWorkloadPredictionService.Predict(data, data.Tokens[^1].ObservedAtUtc.AddMinutes(20));
        var withQuota = data with { Quota = [new(TajsTokens.Core.Enums.QuotaWindowKind.Weekly, Start, 90, 10080, Start.AddDays(7), "codex", "default", "codex-app-server:codex")] };
        Assert.Equal(JsonSerializer.Serialize(forecast), JsonSerializer.Serialize(
            TokenWorkloadPredictionService.Predict(withQuota, data.Tokens[^1].ObservedAtUtc.AddMinutes(20))));
        Assert.Equal(140, forecast.TokenEvents);
        Assert.Equal(14, forecast.Sessions);
        Assert.Equal(2, forecast.Predictions.Count);
        Assert.All(forecast.Predictions, x => Assert.True(x.TrainingSamples >= 20));
        Assert.Contains("not proof", forecast.Methodology);
    }

    [Fact]
    public void NativeTokenModelLearnsHeldOutRelationshipAndBeatsPace()
    {
        var data = History(180);
        var trials = TokenWorkloadPredictionService.Replay(data, 0.5);
        Assert.Contains(trials, x => x.Prediction.Model == "workload-ridge");
        var scores = TokenWorkloadPredictionService.EvaluateScores(data);
        var baseline = scores.Single(x => x.HorizonHours == 0.5 && x.Model == "recent-30m");
        var selected = scores.Single(x => x.HorizonHours == 0.5 && x.Model == TokenWorkloadPredictionService.PolicyVersion);
        Assert.True(selected.MeanAbsoluteError < baseline.MeanAbsoluteError);
    }

    [Fact]
    public void FutureTokensAndFutureModelMetadataDoNotChangeEarlierPredictions()
    {
        var data = History(90);
        var before = TokenWorkloadPredictionService.Replay(data, 0.5);
        var future = History(130);
        var after = TokenWorkloadPredictionService.Replay(future, 0.5);
        foreach (var trial in before)
        {
            var matching = after.Single(x => x.OriginUtc == trial.OriginUtc);
            Assert.Equal(JsonSerializer.Serialize(trial), JsonSerializer.Serialize(matching));
        }
        var now = Start.AddHours(43);
        Assert.Equal(JsonSerializer.Serialize(TokenWorkloadPredictionService.Predict(data, now)),
            JsonSerializer.Serialize(TokenWorkloadPredictionService.Predict(future, now)));
    }

    [Fact]
    public void BackfilledRolloutsAreUsableForRetrospectiveTrainingButNotBeforeCollection()
    {
        var data = History(100);
        var now = data.Tokens[^1].ObservedAtUtc.AddMinutes(20);
        var backfilled = data with { Tokens = data.Tokens.Select(x => x with { CapturedAtUtc = now }).ToArray() };
        var prediction = TokenWorkloadPredictionService.Predict(backfilled, now);
        Assert.True(prediction.Predictions[0].TrainingSamples >= 20);
        Assert.Empty(TokenWorkloadPredictionService.Predict(backfilled, now.AddMinutes(-1)).Predictions);
    }

    [Fact]
    public void SparseOrIdleInputsDoNotClaimACalibratedRange()
    {
        var data = History(8);
        var now = data.Tokens[^1].ObservedAtUtc;
        Assert.All(TokenWorkloadPredictionService.Predict(data, now).Predictions, x => Assert.Null(x.LowerTokens));
        Assert.Empty(TokenWorkloadPredictionService.Predict(data, now.AddHours(3)).Predictions);
        Assert.Empty(TokenWorkloadPredictionService.Predict(data with { Tokens = [] }, now).Predictions);
    }

    [Fact]
    public void SubTickHorizonIsRejectedBeforeGridArithmetic() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenWorkloadPredictionService.Replay(History(8), double.Epsilon));

    private static CodexForecastDataset History(int count)
    {
        var tokens = Enumerable.Range(0, count).Select(i =>
        {
            var at = Start.AddMinutes(i * 30 + 10);
            var high = i % 2 == 0;
            return new CodexPredictiveTokenEvent($"session-{i / 10}", at, at,
                i >= 100 ? "new-model" : "model-a", high ? "high" : "low",
                high ? 10000 : 100, 0, 0, 0, 0, high ? 10000 : 100);
        }).ToArray();
        return new([], [], tokens, [], tokens[^1].ObservedAtUtc, "Synthetic token pattern, not empirical performance");
    }
}
