using TajsTokens.Infrastructure.Ingestion;

namespace TajsTokens.Core.Tests;

public sealed class CodexDesktopFilenameTests
{
    private const string Owner = "11111111-1111-4111-8111-111111111111";
    private const string Suffix = "22222222-2222-4222-8222-222222222222";
    private const string Path = $"rollout-2026-09-19T10-00-00-{Owner}_{Suffix}.jsonl";

    [Fact]
    public void KnownDesktopShapeUsesThreadUuidNotFileSuffix()
    {
        Assert.Equal(Owner, CodexRolloutParser.ExtractSessionIdFromFileName(Path));
        Assert.Equal(Owner, CodexRolloutParser.ExtractSessionIdFromFileName($"rollout-{Owner}.jsonl"));
        Assert.Equal(Suffix, CodexRolloutParser.ExtractSessionIdFromFileName($"renamed-{Owner}_{Suffix}.jsonl"));
        Assert.False(CodexRolloutParser.HasDesktopFilenameSuffix($"renamed-{Owner}_{Suffix}.jsonl"));
    }

    [Fact]
    public void SuffixMetadataCannotClaimOwnershipAndCopiedPrefixRemainsExcluded()
    {
        var parser = new CodexRolloutParser();
        var state = new RolloutParseState(Path, "source");
        string Meta(string id) => System.Text.Json.JsonSerializer.Serialize(new { type = "session_meta", payload = new { id } });
        const string tokens = """{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":10,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":0,"reasoning_output_tokens":0,"total_tokens":10}}}}""";
        parser.Parse(new(Path, 0, 1, Meta(Suffix)), state);
        Assert.False(state.OwnershipEstablished);
        Assert.Null(parser.Parse(new(Path, 1, 2, tokens), state).TokenObservation);
        parser.Parse(new(Path, 2, 3, Meta(Owner)), state);
        var owned = parser.Parse(new(Path, 3, 4, tokens), state).TokenObservation;
        Assert.Equal(Owner, owned!.SessionId);
        Assert.Equal(10, owned.TotalTokens);
        Assert.Equal(Owner, new RolloutParseState(Path, "source", state.BuildResumeState(4)).OwnSessionId);
    }
}
