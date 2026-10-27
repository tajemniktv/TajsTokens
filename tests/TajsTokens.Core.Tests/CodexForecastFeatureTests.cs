// Taj's Tokens | CodexForecastFeatureTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class CodexForecastFeatureTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-09-01T12:00:00Z");

    [Fact]
    public void IndexedFeaturesMatchFullHistoryWithOldStartsAndLateEvidence()
    {
        CodexWorkloadObservation[] metadata =
        [
            Observation("old", "session_meta", -10000) with { SessionSourceKind = "cli" },
            Observation("old", "task_started", -9000), Observation("old", "task_complete", -8000),
            Observation("root", "session_meta", -1000) with { SessionSourceKind = "vscode" },
            Observation("child", "session_meta", -1000) with { SessionSourceKind = "subagent", ParentThreadId = "root" },
            Observation("root", "task_started", -500), Observation("root", "task_complete", -20),
            Observation("child", "task_started", -150), Observation("child", "turn_context", -10),
            Observation("child", "task_complete", -5) with { CapturedAtUtc = Origin.AddHours(1) },
        ];
        var token = new CodexPredictiveTokenEvent("root", Origin.AddMinutes(-5), Origin, "m", "high", 10, 20, 0, 6, 4, 40);
        var context = new CodexContextObservation("ctx", "child", null, Origin.AddMinutes(-3), "m", 10, 100, false,
            CapturedAtUtc: Origin.AddHours(1));
        var data = new CodexForecastDataset([], metadata.Reverse().ToArray(),
            [token, token with { SessionId = "child" }, token with { ObservedAtUtc = Origin.AddDays(-1) }],
            [context, context with { EventId = "ctx2", InputTokens = 20 }], Origin, "fixture");
        var index = new CodexForecastFeatureIndex(data);
        foreach (ForecastReplayAvailability availability in Enum.GetValues<ForecastReplayAvailability>())
        foreach (int offset in new[] { -200, -20, 0, 60, 200 })
        {
            DateTimeOffset at = Origin.AddMinutes(offset);
            Assert.Equal(JsonSerializer.Serialize(CodexForecastFeatureBuilder.Build(data, at, availability: availability)),
                JsonSerializer.Serialize(index.Build(at, availability)));
        }
    }

    [Fact]
    public void FutureCompletionsAndFutureModelsDoNotRewriteHistoricalActivity()
    {
        CodexWorkloadObservation[] metadata = new[]
        {
            Observation("root", "session_meta", -60) with { SessionSourceKind = "vscode" },
            Observation("child", "session_meta", -60) with { SessionSourceKind = "subagent", ParentThreadId = "root" },
            Observation("root", "task_started", -20),
            Observation("child", "task_started", -10),
        };
        var token = new CodexPredictiveTokenEvent(
            "root",
            Origin.AddMinutes(-5),
            Origin.AddMinutes(-4),
            "model-a",
            "high",
            10,
            20,
            0,
            6,
            4,
            40);
        var data = new CodexForecastDataset([], metadata, [token], [], Origin, "fixture");
        CodexForecastFeatures before = CodexForecastFeatureBuilder.Build(data, Origin);
        CodexForecastFeatures after = CodexForecastFeatureBuilder.Build(
            data with
            {
                Workload =
                metadata.Append(Observation("root", "task_complete", 10)).Append(Observation("child", "task_complete", 20)).ToArray(),
                Tokens =
                [
                    token, token with { ObservedAtUtc = Origin.AddMinutes(1), Model = "future-model", ReportedTotalTokens = 999999 },
                ],
            },
            Origin);
        Assert.Equal(2, before.ObservedOpenTurns);
        Assert.Equal(2, after.PeakObservedTurnOverlap);
        Assert.Equal(before.Tokens, after.Tokens);
        Assert.Equal(1, after.TokenActiveRootSessions);
        Assert.Equal(0, after.CompletedTurns);
        Assert.False(after.ModelTokenShares.ContainsKey("future-model"));
        Assert.Equal(2d / 3, after.CacheReadShare!.Value, 5);
    }

    [Fact]
    public void BackfillAvailabilityIsDistinctFromEventTime()
    {
        CodexWorkloadObservation[] metadata = new[] { Observation("root", "task_started", -20) with { CapturedAtUtc = Origin.AddDays(1) } };
        var token = new CodexPredictiveTokenEvent("root", Origin.AddMinutes(-5), null, "model-a", "high", 10, 0, 0, 0, 0, 10);
        var data = new CodexForecastDataset([], metadata, [token], [], Origin.AddDays(1), "fixture");
        CodexForecastFeatures reconstructed = CodexForecastFeatureBuilder.Build(data, Origin);
        CodexForecastFeatures strict = CodexForecastFeatureBuilder.Build(
            data,
            Origin,
            availability: ForecastReplayAvailability.CollectedByOrigin);
        Assert.Equal(1, reconstructed.ObservedOpenTurns);
        Assert.Equal(10, reconstructed.Tokens);
        Assert.Equal(0, strict.ObservedOpenTurns);
        Assert.Equal(0, strict.ObservedTokenEvents);
        Assert.Null(strict.CacheReadShare);
    }

    private static CodexWorkloadObservation Observation(string session, string type, int minutes)
    {
        return new CodexWorkloadObservation(
            session + type,
            "source",
            "safe.jsonl",
            0,
            1,
            session,
            type,
            Origin.AddMinutes(minutes),
            Origin.AddMinutes(minutes),
            type == "session_meta" ? null : session + "-turn",
            null,
            null,
            null,
            null,
            null);
    }
}
