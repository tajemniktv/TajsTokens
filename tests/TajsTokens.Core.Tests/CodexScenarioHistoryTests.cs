using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexScenarioHistoryTests
{
    [Fact]
    public void Builder_UsesOriginFeatures_RetainsFlatTargets_ExcludesEmbeddedAndResetCrossings()
    {
        var start = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        QuotaSnapshot Quota(int minutes, double used, int reset = 300, string source = "codex-app-server:test") =>
            new(QuotaWindowKind.FiveHour, start.AddMinutes(minutes), used, 300, start.AddMinutes(reset), "codex", "default", source);
        var root = new CodexWorkloadObservation("meta", "file", "safe.jsonl", 0, 1, "root", "session_meta", start.AddMinutes(-20), start.AddMinutes(-20), null, null, null, null, null, null, SessionSourceKind: "vscode");
        var token = new CodexPredictiveTokenEvent("root", start.AddMinutes(-10), null, "old", "high", 10, 0, 0, 0, 0, 10);
        var data = new CodexForecastDataset(
            [Quota(0, 20), Quota(30, 20), Quota(60, 21), Quota(90, 0, 390), Quota(15, 80, source: "codex-rollout:test")],
            [root], [token, token with { ObservedAtUtc = start.AddMinutes(10), Model = "future" }], [], start.AddHours(2), "fixture");
        var samples = CodexScenarioHistoryBuilder.Build(data);
        Assert.Equal(2, samples.Count);
        Assert.Equal(0, samples[0].QuotaDeltaPercent);
        Assert.Equal("old", samples[0].DominantModel);
        Assert.Null(samples[1].DominantModel);
        Assert.All(samples, x => Assert.Equal("codex-app-server:test", x.Source));
        Assert.All(samples, x => Assert.Equal(1, x.RootAgents));
    }
}
