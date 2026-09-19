using System.Text.Json;
using TajsTokens.Infrastructure.Ingestion;

namespace TajsTokens.Core.Tests;

public sealed class CodexResponseUsageComparisonTests
{
    [Fact]
    public void ComparesVectorsWithoutExportingIdentityOrSummingStreams()
    {
        var audit = new CodexResponseUsageComparison();
        audit.Observe(Response("private-response", 100), "private-thread");
        audit.Observe(Token(100), "private-thread");
        audit.Observe(Response("private-other", 200), "private-thread");
        audit.Observe(Token(50), "private-thread");
        audit.Complete();
        Assert.Equal(1, audit.Counts["exact-vector-pair"]);
        Assert.Equal(1, audit.Counts["different-vector-pair"]);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(audit));
    }

    [Fact]
    public void LifecycleBoundaryAndMultipleCandidatesCannotBecomeExactPairs()
    {
        var audit = new CodexResponseUsageComparison();
        audit.Observe(Response("one", 100), "private-thread");
        audit.Observe("""{"type":"event_msg","payload":{"type":"task_started"}}""", "private-thread");
        audit.Observe(Token(100), "private-thread");
        audit.Observe(Response("two", 100), "private-thread");
        audit.Observe(Response("three", 100), "private-thread");
        audit.Observe(Token(100), "private-thread");
        audit.Observe(Response("four", 100), "private-thread");
        audit.Complete();
        audit.Complete();
        Assert.Equal(1, audit.Counts["response-records-unpaired-at-boundary"]);
        Assert.Equal(1, audit.Counts["legacy-without-pending-response"]);
        Assert.Equal(1, audit.Counts["ambiguous-multiple-response-pair"]);
        Assert.Equal(1, audit.Counts["response-records-unpaired-at-end"]);
        Assert.False(audit.Counts.ContainsKey("exact-vector-pair"));
    }

    [Fact]
    public void RepeatedConflictingMissingAndWrongOwnerIdentityRemainIneligible()
    {
        var audit = new CodexResponseUsageComparison();
        foreach (var response in new[] { Response("one", 100), Response("one", 100),
                     Response("one", 200), Response(null, 100), Response("other", 100, "wrong-owner") })
        {
            audit.Observe(response, "private-thread");
            audit.Observe(Token(100), "private-thread");
        }
        Assert.Equal(1, audit.Counts["exact-vector-pair"]);
        Assert.Equal(1, audit.Counts["repeated-response-key"]);
        Assert.Equal(1, audit.Counts["conflicting-response-key"]);
        Assert.Equal(1, audit.Counts["missing-identity"]);
        Assert.Equal(1, audit.Counts["owner-conflict"]);
        Assert.Equal(4, audit.Counts["ineligible-response-pair"]);
    }

    [Fact]
    public void AbsentCacheWriteDefaultsButNullAndMissingRequiredCountersDoNot()
    {
        var audit = new CodexResponseUsageComparison();
        audit.Observe(Response("one", 100), "private-thread");
        audit.Observe(Token(100).Replace("\"output_tokens\":0", "\"cache_write_input_tokens\":0,\"output_tokens\":0"), "private-thread");
        audit.Observe(Response("two", 100).Replace("\"output_tokens\":0", "\"cache_write_input_tokens\":null,\"output_tokens\":0"), "private-thread");
        audit.Observe(Token(100), "private-thread");
        audit.Observe(Response("three", 100), "private-thread");
        audit.Observe(Token(100).Replace("\"output_tokens\":0,", ""), "private-thread");
        Assert.Equal(1, audit.Counts["exact-vector-pair"]);
        Assert.Equal(1, audit.Counts["invalid-response-vector"]);
        Assert.Equal(1, audit.Counts["invalid-legacy-vector-pair"]);
    }

    private static object Usage(long total) => new { input_tokens = total, cached_input_tokens = 0,
        output_tokens = 0, reasoning_output_tokens = 0, total_tokens = total };
    private static string Response(string? id, long total, string owner = "private-thread") =>
        JsonSerializer.Serialize(new { type = "token_usage_record", payload = new { thread_id = owner,
            response_id = id, turn_id = "private-turn", session_id = "private-session", root_turn_id = "private-root",
            usage = Usage(total) } });
    private static string Token(long total) => JsonSerializer.Serialize(new { type = "event_msg",
        payload = new { type = "token_count", info = new { last_token_usage = Usage(total) } } });
}
