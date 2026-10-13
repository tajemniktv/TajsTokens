// Taj's Tokens | CodexRolloutParserTokenTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class CodexRolloutParserTokenTests
{
    [Fact]
    public void TokenCount_ParsesCumulativeAndOptionalLastUsageSnapshots()
    {
        const string sessionId = "11111111-1111-4111-8111-111111111111";
        string file = $"rollout-2026-09-01T10-00-00-{sessionId}.jsonl";
        var state = new RolloutParseState(file, "source");
        var parser = new CodexRolloutParser();

        parser.Parse(
            new RawSessionRecord(
                file,
                0,
                100,
                $"{{\"timestamp\":\"2026-09-01T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{sessionId}\"}}}}"),
            state);

        ParsedRolloutRecord parsed = parser.Parse(
            new RawSessionRecord(
                file,
                100,
                500,
                """
                {"timestamp":"2026-09-01T10:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":80,"cache_write_input_tokens":2,"output_tokens":10,"reasoning_output_tokens":4,"total_tokens":110},"last_token_usage":{"input_tokens":20,"cached_input_tokens":15,"cache_write_input_tokens":1,"output_tokens":5,"reasoning_output_tokens":2,"total_tokens":25}}}}
                """),
            state);

        Assert.NotNull(parsed.TokenObservation);
        CodexTokenCountObservation observation = parsed.TokenObservation!;
        Assert.Equal(100, observation.InputTokens);
        Assert.Equal(110, observation.TotalTokens);
        Assert.NotNull(observation.LastTokenUsage);
        Assert.Equal(20, observation.LastTokenUsage!.InputTokens);
        Assert.Equal(1, observation.LastTokenUsage.CacheWriteInputTokens);
        Assert.Equal(25, observation.LastTokenUsage.TotalTokens);
    }

    [Fact]
    public void TokenCount_LastOnlyStillProducesContextInput()
    {
        const string sessionId = "22222222-2222-4222-8222-222222222222";
        string file = $"rollout-2026-09-01T10-00-00-{sessionId}.jsonl";
        var state = new RolloutParseState(file, "source");
        var parser = new CodexRolloutParser();

        parser.Parse(
            new RawSessionRecord(
                file,
                0,
                100,
                $"{{\"timestamp\":\"2026-09-01T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{sessionId}\"}}}}"),
            state);

        ParsedRolloutRecord parsed = parser.Parse(
            new RawSessionRecord(
                file,
                100,
                300,
                """
                {"timestamp":"2026-09-01T10:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":12,"cached_input_tokens":4,"cache_write_input_tokens":1,"output_tokens":3,"reasoning_output_tokens":1,"total_tokens":16}}}}
                """),
            state);

        Assert.NotNull(parsed.TokenObservation);
        Assert.Null(parsed.TokenObservation!.TotalTokenUsage);
        Assert.Equal(12, parsed.ContextObservation!.InputTokens);
    }

    [Fact]
    public void TokenCount_UsesValidRootTimestampBeforeNestedAndAcceptsNestedFallback()
    {
        const string sessionId = "33333333-3333-4333-8333-333333333333";
        string file = $"rollout-2026-09-01T10-00-00-{sessionId}.jsonl";
        var state = new RolloutParseState(file, "source");
        var parser = new CodexRolloutParser();
        parser.Parse(
            new RawSessionRecord(file, 0, 100,
                $"{{\"timestamp\":\"2026-09-01T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{sessionId}\"}}}}"),
            state);

        ParsedRolloutRecord rootWins = parser.Parse(
            new RawSessionRecord(file, 100, 300,
                """
                {"timestamp":"2026-09-01T10:00:02Z","type":"event_msg","payload":{"timestamp":"2026-09-01T10:00:01Z","type":"token_count","info":{"last_token_usage":{"input_tokens":12,"cached_input_tokens":4,"cache_write_input_tokens":1,"output_tokens":3,"reasoning_output_tokens":1,"total_tokens":16}}}}
                """),
            state);

        ParsedRolloutRecord nestedFallback = parser.Parse(
            new RawSessionRecord(file, 300, 500,
                """
                {"type":"event_msg","payload":{"timestamp":"2026-09-01T10:00:03Z","type":"token_count","info":{"last_token_usage":{"input_tokens":12,"cached_input_tokens":4,"cache_write_input_tokens":1,"output_tokens":3,"reasoning_output_tokens":1,"total_tokens":16}}}}
                """),
            state);

        Assert.Equal(DateTimeOffset.Parse("2026-09-01T10:00:02Z"), rootWins.TokenObservation!.SourceEventAtUtc);
        Assert.Equal(CodexSourceTimestampQuality.Valid, rootWins.TokenObservation.SourceTimestampQuality);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T10:00:03Z"), nestedFallback.TokenObservation!.SourceEventAtUtc);
        Assert.Equal(CodexSourceTimestampQuality.Valid, nestedFallback.TokenObservation.SourceTimestampQuality);
    }

    [Fact]
    public void TokenCount_MissingOrMalformedTimestampKeepsCaptureAndDoesNotInventEventTime()
    {
        const string sessionId = "44444444-4444-4444-8444-444444444444";
        string file = $"rollout-2026-09-01T10-00-00-{sessionId}.jsonl";
        var state = new RolloutParseState(file, "source");
        var parser = new CodexRolloutParser();
        parser.Parse(
            new RawSessionRecord(file, 0, 100,
                $"{{\"timestamp\":\"2026-09-01T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{sessionId}\"}}}}"),
            state);

        ParsedRolloutRecord missing = parser.Parse(
            new RawSessionRecord(file, 100, 300,
                """
                {"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":12,"cached_input_tokens":4,"cache_write_input_tokens":1,"output_tokens":3,"reasoning_output_tokens":1,"total_tokens":16}}}}
                """),
            state);
        ParsedRolloutRecord malformed = parser.Parse(
            new RawSessionRecord(file, 300, 500,
                """
                {"timestamp":"not-a-time","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":12,"cached_input_tokens":4,"cache_write_input_tokens":1,"output_tokens":3,"reasoning_output_tokens":1,"total_tokens":16}}}}
                """),
            state);

        Assert.Null(missing.TokenObservation!.SourceEventAtUtc);
        Assert.Equal(CodexSourceTimestampQuality.Missing, missing.TokenObservation.SourceTimestampQuality);
        Assert.NotEqual(default, missing.TokenObservation.CapturedAtUtc);
        Assert.Null(missing.ContextObservation!.SourceEventAtUtc);
        Assert.Equal(CodexSourceTimestampQuality.Missing, missing.ContextObservation.SourceTimestampQuality);
        Assert.Null(missing.UsageEvent);
        Assert.Null(malformed.TokenObservation!.SourceEventAtUtc);
        Assert.Equal(CodexSourceTimestampQuality.Malformed, malformed.TokenObservation.SourceTimestampQuality);
        Assert.NotEqual(default, malformed.TokenObservation.CapturedAtUtc);
    }
}
