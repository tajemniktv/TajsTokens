// Taj's Tokens | CodexLiveFleetPolicyTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using TajsTokens.Core.Models;

namespace TajsTokens.Core.Tests;

public sealed class CodexLiveFleetPolicyTests
{
    [Fact]
    public void InProgressRequiresFreshObservedWrite()
    {
        DateTimeOffset now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(CodexFleetState.RecentlyWorking,
            CodexLiveFleetPolicy.Classify("inProgress", now.AddSeconds(-15), now));
        Assert.Equal(CodexFleetState.StaleInProgress,
            CodexLiveFleetPolicy.Classify("inProgress", now.AddHours(-1), now));
        Assert.Equal(CodexFleetState.StaleInProgress,
            CodexLiveFleetPolicy.Classify("inProgress", null, now));
        Assert.Equal(CodexFleetState.StaleInProgress,
            CodexLiveFleetPolicy.Classify("inProgress", now.AddMinutes(2), now));
    }

    [Theory]
    [InlineData("completed", CodexFleetState.ReadyForPrompt)]
    [InlineData("interrupted", CodexFleetState.Ended)]
    [InlineData("failed", CodexFleetState.Ended)]
    [InlineData("waitingForInput", CodexFleetState.Unknown)]
    [InlineData(null, CodexFleetState.Unknown)]
    public void PreservesNativeStatusBoundaries(string? status, CodexFleetState expected)
    {
        Assert.Equal(expected, CodexLiveFleetPolicy.Classify(status, null, DateTimeOffset.UtcNow));
    }
}
