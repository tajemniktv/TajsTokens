using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Infrastructure.Providers;

namespace TajsTokens.Core.Tests;

public sealed class ProviderParsingTests
{
    [Fact]
    public void TokscaleModels_ParsesDisjointBucketsWithoutDoubleCounting()
    {
        const string json = """
            {
              "entries": [
                {
                  "client": "codex",
                  "model": "gpt-5.6-luna",
                  "input": 120,
                  "cacheRead": 900,
                  "cacheWrite": 10,
                  "output": 40,
                  "reasoning": 30,
                  "total": 1100
                }
              ]
            }
            """;

        var usage = Assert.Single(TokscaleProvider.ParseModelUsageJson(json, DateTimeOffset.UnixEpoch));

        Assert.Equal(120, usage.Breakdown.UncachedInput);
        Assert.Equal(900, usage.Breakdown.CacheRead);
        Assert.Equal(10, usage.Breakdown.CacheWrite);
        Assert.Equal(40, usage.Breakdown.NonReasoningOutput);
        Assert.Equal(30, usage.Breakdown.ReasoningOutput);
        Assert.Equal(1100, usage.Breakdown.Total);
    }

    [Fact]
    public void TokscaleModels_NormalizesOlderReasoningInclusiveOutputShape()
    {
        const string json = """
            {
              "entries": [
                {
                  "client": "codex",
                  "model": "gpt-5.6-luna",
                  "input": 100,
                  "cacheRead": 800,
                  "cacheWrite": 0,
                  "output": 70,
                  "reasoning": 20,
                  "total": 970
                }
              ]
            }
            """;

        var usage = Assert.Single(TokscaleProvider.ParseModelUsageJson(json, DateTimeOffset.UnixEpoch));

        Assert.Equal(50, usage.Breakdown.NonReasoningOutput);
        Assert.Equal(20, usage.Breakdown.ReasoningOutput);
        Assert.Equal(970, usage.Breakdown.Total);
    }

    [Fact]
    public void TokscaleModels_RejectsUnsupportedObjectShape()
    {
        const string json = """{ "unexpected": [] }""";

        var exception = Assert.Throws<JsonException>(() =>
            TokscaleProvider.ParseModelUsageJson(json, DateTimeOffset.UnixEpoch));

        Assert.Contains("supported entry array", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TokscaleModels_AllowsAValidEmptyEntriesArray()
    {
        const string json = """{ "entries": [] }""";

        Assert.Empty(TokscaleProvider.ParseModelUsageJson(json, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void TokscaleModels_RejectsMalformedRowsInsteadOfFabricatingZeroUsage()
    {
        const string json = """
            {
              "entries": [
                { "client": "codex", "somethingElse": 123 }
              ]
            }
            """;

        var exception = Assert.Throws<JsonException>(() =>
            TokscaleProvider.ParseModelUsageJson(json, DateTimeOffset.UnixEpoch));

        Assert.Contains("recognized token fields", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TokscaleHourly_AcceptsEntriesAndNumericHourLabels()
    {
        const string json = """
            {
              "entries": [
                { "date": "2026-08-31", "hour": 3, "input": 10, "cacheRead": 90, "output": 4, "reasoning": 1, "total": 105 }
              ]
            }
            """;

        var bucket = Assert.Single(TokscaleProvider.ParseHourlyJson(json));

        Assert.Equal("tokscale", bucket.Provider);
        Assert.Equal("2026-08-31 03:00", bucket.Label);
        Assert.Equal(105, bucket.Breakdown.Total);
    }

    [Fact]
    public void TokscaleHourly_RejectsMalformedRows()
    {
        const string json = """
            {
              "entries": [
                { "date": "2026-08-31", "hour": 3, "unexpected": 105 }
              ]
            }
            """;

        var exception = Assert.Throws<JsonException>(() => TokscaleProvider.ParseHourlyJson(json));

        Assert.Contains("recognized token fields", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsCommandLine_DoesNotPreserveQuotesAroundTokscaleGroupByValue()
    {
        var commandLine = ExternalProcess.BuildWindowsCommandLine(
            "tokscale",
            ["models", "--json", "--group-by", "client,model", "--client", "codex"]);

        Assert.Contains("--group-by client,model", commandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("\"client,model\"", commandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalProcess_ConcreteExecutablePathBypassesShellExpansion()
    {
        const string pathWithPercentSequence = @"C:\Tools\%TEMP%\codex.exe";

        var startInfo = ExternalProcess.CreateStartInfo(
            pathWithPercentSequence,
            ["app-server", "--listen", "stdio://"]);

        Assert.Equal(pathWithPercentSequence, startInfo.FileName);
        Assert.Equal(["app-server", "--listen", "stdio://"], startInfo.ArgumentList.ToArray());
    }

    [Fact]
    public void TokscaleCommandDiscovery_FallsBackOnlyForMissingCommand()
    {
        var missing = new ExternalCommandResult(
            1,
            string.Empty,
            "'tokscale' is not recognized as an internal or external command, operable program or batch file.");
        var realCliError = new ExternalCommandResult(
            127,
            string.Empty,
            "Error: a Tokscale dependency exited with status 127");
        var missingNpx = new ExternalCommandResult(
            1,
            string.Empty,
            "'npx' is not recognized as an internal or external command, operable program or batch file.");

        Assert.True(TokscaleProvider.LooksLikeCommandNotFound(missing, "tokscale"));
        Assert.False(TokscaleProvider.LooksLikeCommandNotFound(realCliError, "tokscale"));
        Assert.True(TokscaleProvider.LooksLikeCommandNotFound(missingNpx, "npx"));
    }

    [Fact]
    public void CodexInitialize_RejectsJsonRpcErrorsImmediately()
    {
        const string json = """
            {
              "id": 0,
              "error": { "code": -32002, "message": "Not authorized" }
            }
            """;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CodexAppServerQuotaProvider.EnsureSuccessfulJsonRpcResponse(json, "initialize"));

        Assert.Contains("initialize failed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Not authorized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CodexRateLimits_MapsWindowsByDurationNotPrimaryPosition()
    {
        const string json = """
            {
              "id": 1,
              "result": {
                "rateLimits": {
                  "primary": { "usedPercent": 45, "windowDurationMins": 10080, "resetsAt": 1788647556 }
                }
              }
            }
            """;

        var snapshot = Assert.Single(CodexAppServerQuotaProvider.ParseRateLimitsResponse(json, DateTimeOffset.UnixEpoch));

        Assert.Equal(QuotaWindowKind.Weekly, snapshot.Kind);
        Assert.Equal(55d, snapshot.RemainingPercent);
        Assert.Equal(10_080, snapshot.WindowMinutes);
    }

    [Fact]
    public void CodexRateLimits_PrefersCodexEntryFromRateLimitsByLimitId()
    {
        const string json = """
            {
              "id": 1,
              "result": {
                "rateLimits": {
                  "primary": { "usedPercent": 99, "windowDurationMins": 300, "resetsAt": 1780000000 }
                },
                "rateLimitsByLimitId": {
                  "other": { "primary": { "usedPercent": 88, "windowDurationMins": 300, "resetsAt": 1780000001 } },
                  "codex": {
                    "primary": { "usedPercent": 25, "windowDurationMins": 300, "resetsAt": 1788060756 },
                    "secondary": { "usedPercent": 40, "windowDurationMins": 10080, "resetsAt": 1788647556 }
                  }
                }
              }
            }
            """;

        var snapshots = CodexAppServerQuotaProvider.ParseRateLimitsResponse(json, DateTimeOffset.UnixEpoch);

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(25d, snapshots.Single(snapshot => snapshot.Kind == QuotaWindowKind.FiveHour).UsedPercent);
        Assert.Equal(40d, snapshots.Single(snapshot => snapshot.Kind == QuotaWindowKind.Weekly).UsedPercent);
    }

    [Fact]
    public void CodexRateLimits_DoesNotMislabelAnotherLimitAsCodex()
    {
        const string json = """
            {
              "id": 1,
              "result": {
                "rateLimitsByLimitId": {
                  "other": {
                    "primary": { "usedPercent": 88, "windowDurationMins": 300, "resetsAt": 1780000001 }
                  }
                }
              }
            }
            """;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CodexAppServerQuotaProvider.ParseRateLimitsResponse(json, DateTimeOffset.UnixEpoch));

        Assert.Contains("identifiable Codex", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CodexRateLimits_FallsBackToLegacyViewWhenNamedCodexBucketIsAbsent()
    {
        const string json = """
            {
              "id": 1,
              "result": {
                "rateLimits": {
                  "primary": { "usedPercent": 20, "windowDurationMins": 300, "resetsAt": 1780000010 }
                },
                "rateLimitsByLimitId": {
                  "other": {
                    "primary": { "usedPercent": 88, "windowDurationMins": 300, "resetsAt": 1780000001 }
                  }
                }
              }
            }
            """;

        var snapshot = Assert.Single(CodexAppServerQuotaProvider.ParseRateLimitsResponse(json, DateTimeOffset.UnixEpoch));

        Assert.Equal(20d, snapshot.UsedPercent);
        Assert.Contains("legacy", snapshot.Source, StringComparison.OrdinalIgnoreCase);
    }
}
