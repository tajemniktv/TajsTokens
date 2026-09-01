using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;

namespace TajsTokens.Core.Tests;

public sealed class CodexRolloutParserTokenTests
{
    [Fact]
    public void TokenCount_ParsesCumulativeAndOptionalLastUsageSnapshots()
    {
        const string sessionId = "11111111-1111-4111-8111-111111111111";
        var file = $"rollout-2026-09-01T10-00-00-{sessionId}.jsonl";
        var state = new RolloutParseState(file, "source");
        var parser = new CodexRolloutParser();

        parser.Parse(new RawSessionRecord(
            file,
            0,
            100,
            $"{{\"timestamp\":\"2026-09-01T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{sessionId}\"}}}}"), state);

        var parsed = parser.Parse(new RawSessionRecord(
            file,
            100,
            500,
            """
            {"timestamp":"2026-09-01T10:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":80,"cache_write_input_tokens":2,"output_tokens":10,"reasoning_output_tokens":4,"total_tokens":110},"last_token_usage":{"input_tokens":20,"cached_input_tokens":15,"cache_write_input_tokens":1,"output_tokens":5,"reasoning_output_tokens":2,"total_tokens":25}}}}
            """), state);

        Assert.NotNull(parsed.TokenObservation);
        var observation = parsed.TokenObservation!;
        Assert.Equal(100, observation.InputTokens);
        Assert.Equal(110, observation.TotalTokens);
        Assert.NotNull(observation.LastTokenUsage);
        Assert.Equal(20, observation.LastTokenUsage!.InputTokens);
        Assert.Equal(1, observation.LastTokenUsage.CacheWriteInputTokens);
        Assert.Equal(25, observation.LastTokenUsage.TotalTokens);
    }
}
