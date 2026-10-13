// Taj's Tokens | CodexRemoteFleetFreshnessTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using TajsTokens.Core.Models;

namespace TajsTokens.Core.Tests;

public sealed class CodexRemoteFleetFreshnessTests
{
    [Fact]
    public void UsesLocalReceiveTimeAndSourceRelativeAgeRatherThanRemoteClock()
    {
        DateTimeOffset local = DateTimeOffset.Parse("2026-10-13T10:00:00Z");
        DateTimeOffset remote = local.AddHours(2);
        var thread = new CodexRemoteFleetThread("laptop", "thread-1", "inProgress", null, remote.AddSeconds(-10));
        var snapshot = new CodexRemoteFleetHostResult("laptop", remote, [thread], null, false)
            { CollectedAtUtc = local };
        Assert.True(CodexRemoteFleetFreshness.WasCheckedRecently(snapshot, local));
        Assert.Equal(CodexFleetState.RecentlyWorking, CodexRemoteFleetFreshness.Classify(snapshot, thread, local));
        Assert.False(CodexRemoteFleetFreshness.WasCheckedRecently(snapshot, local.AddHours(1)));
        Assert.Equal(CodexFleetState.StaleInProgress,
            CodexRemoteFleetFreshness.Classify(snapshot, thread, local.AddHours(1)));
    }

    [Fact]
    public void ImplausibleRemoteTimesCannotClaimRecentWork()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-13T10:00:00Z");
        var thread = new CodexRemoteFleetThread("server", "thread-1", "inProgress", null, now.AddDays(2));
        var snapshot = new CodexRemoteFleetHostResult("server", now, [thread], null, false)
            { CollectedAtUtc = now };
        Assert.Equal(CodexFleetState.StaleInProgress, CodexRemoteFleetFreshness.Classify(snapshot, thread, now));
    }
}
