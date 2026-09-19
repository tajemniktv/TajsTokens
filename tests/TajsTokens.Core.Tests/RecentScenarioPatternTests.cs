using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class RecentScenarioPatternTests
{
    [Fact]
    public void SuggestsClassifiedLocalPatternWithoutRenormalizingUnknownMix()
    {
        var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");
        CodexWorkloadObservation Meta(string id, string kind) => new(id, "source", "file", 0, 1, id,
            "session_meta", now.AddDays(-1), now.AddDays(-1), null, null, null, null, null, null, SessionSourceKind: kind);
        CodexPredictiveTokenEvent Token(string id, string? model) => new(id, now.AddMinutes(-1), now.AddSeconds(-30), model, "low", 100, 0, 0, 0, 0, 100);
        var data = new CodexForecastDataset([], [Meta("root", "cli"), Meta("child", "subagent")],
            [Token("root", "m"), Token("child", "m")], [], now, "fixture");
        var pattern = RecentScenarioPatternBuilder.Build(data, now);
        Assert.True(pattern.Available);
        Assert.Equal(1, pattern.RootSessions);
        Assert.Equal(1, pattern.SubagentSessions);
        Assert.Equal("m", pattern.Model);
        Assert.Equal("low", pattern.ReasoningEffort);
        Assert.Contains("Duration is unchanged", pattern.Explanation);
        var mixed = RecentScenarioPatternBuilder.Build(data with { Tokens = [Token("root", "m"), Token("child", null)] }, now);
        Assert.True(mixed.Available);
        Assert.Null(mixed.Model);
        Assert.Contains("filter was cleared", mixed.Explanation);
        Assert.False(RecentScenarioPatternBuilder.Build(data with { Workload = [Meta("root", "cli")] }, now).Available);
        // Late role evidence must not classify a session at this origin.
        Assert.False(RecentScenarioPatternBuilder.Build(data with { Workload = data.Workload.Select(x => x with { CapturedAtUtc = now.AddSeconds(1) }).ToArray() }, now).Available);
        Assert.False(RecentScenarioPatternBuilder.Build(data with { CapturedAtUtc = now.AddMinutes(-11) }, now).Available);
        Assert.False(RecentScenarioPatternBuilder.Build(data with { Tokens = data.Tokens.Select(x => x with { ObservedAtUtc = now.AddMinutes(-11) }).ToArray() }, now).Available);
        Assert.False(RecentScenarioPatternBuilder.Build(data with { Tokens = data.Tokens.Select(x => x with { CapturedAtUtc = null }).ToArray() }, now).Available);
        Assert.False(RecentScenarioPatternBuilder.Build(data with { Tokens = data.Tokens.Select(x => x with { CapturedAtUtc = now.AddSeconds(1) }).ToArray() }, now).Available);
        Assert.False(RecentScenarioPatternBuilder.Build(data with { Tokens = [] }, now).Available);
    }
}
