// Taj's Tokens | SshCodexRemoteFleetServiceTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class SshCodexRemoteFleetServiceTests
{
    [Fact]
    public void Parse_PreservesHostAndNativeStateWithoutAccountOrContentFields()
    {
        const string json = """
                            {"v":1,"captured":1791892800,"threads":[{"id":"thread-1","status":"inProgress","started":1791892700,"observed":1791892790}],"truncated":false,"source_errors":2,"history_available":true}
                            """;
        var snapshot = SshCodexRemoteFleetService.Parse("laptop", json);
        Assert.Equal("laptop", snapshot.Host);
        Assert.Equal(2, snapshot.SourceErrors);
        Assert.True(snapshot.HistoryCapabilityAvailable);
        Assert.Null(snapshot.Error);
        var thread = Assert.Single(snapshot.Threads);
        Assert.Equal("laptop", thread.Host);
        Assert.Equal("thread-1", thread.ThreadId);
        Assert.Equal("inProgress", thread.NativeStatus);
        Assert.Equal(1791892790, thread.LastObservedAtUtc?.ToUnixTimeSeconds());
    }

    [Fact]
    public void Parse_RejectsUnsupportedContract()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => SshCodexRemoteFleetService.Parse(
            "server", "{\"v\":2,\"captured\":0,\"threads\":[],\"truncated\":false,\"source_errors\":0,\"history_available\":false}"));
    }

    [Fact]
    public void Parse_DropsContentBearingIdsAndNormalizesUnknownStatuses()
    {
        const string json = """
                            {"v":1,"captured":1791892800,"threads":[{"id":"prompt text with spaces","status":"inProgress"},{"id":"thread-2","status":"command body"}],"truncated":false,"source_errors":-4,"history_available":true}
                            """;
        var snapshot = SshCodexRemoteFleetService.Parse("server", json);
        var thread = Assert.Single(snapshot.Threads);
        Assert.Equal("thread-2", thread.ThreadId);
        Assert.Equal("unknown", thread.NativeStatus);
        Assert.Equal(0, snapshot.SourceErrors);
        Assert.NotNull(snapshot.CollectedAtUtc);
    }
}
