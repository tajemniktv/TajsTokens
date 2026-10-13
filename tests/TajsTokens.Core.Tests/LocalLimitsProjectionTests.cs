// Taj's Tokens | LocalLimitsProjectionTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class LocalLimitsProjectionTests
{
    [Fact]
    public void EmptySnapshot_IsNotPresentedAsZeroUsage()
    {
        LocalLimitsEnvelope result = LocalLimitsProjection.Build(TelemetrySnapshot.Empty, "salt", DateTimeOffset.UtcNow);
        Assert.Equal("not-collected", result.Status);
        Assert.Null(result.LastRefreshAtUtc);
        Assert.Empty(result.Resources);
    }

    [Fact]
    public void SourceQualifiedLanes_PreserveMissingAndStaleWithoutLeakingAccountKey()
    {
        DateTimeOffset observed = DateTimeOffset.Parse("2026-10-13T08:00:00Z");
        var quota = new QuotaSnapshot(QuotaWindowKind.Weekly, observed, 42, 10080,
            observed.AddDays(4), "codex", "default", "codex-app-server", "private-account-key")
        {
            LimitId = "weekly", CollectedAtUtc = observed.AddSeconds(2), HasSourceTimestamp = true,
        };
        TelemetrySnapshot snapshot = TelemetrySnapshot.Empty with
        {
            CapturedAtUtc = observed.AddSeconds(3),
            QuotaLanes =
            [
                new QuotaLaneState(QuotaWindowKind.Weekly, "codex", "default", quota, TelemetryHealthState.Stale),
                new QuotaLaneState(QuotaWindowKind.FiveHour, "codex", "default", null, TelemetryHealthState.Unavailable),
            ],
        };

        LocalLimitsEnvelope result = LocalLimitsProjection.Build(snapshot, "local-secret", observed.AddMinutes(1));
        Assert.Equal("available", result.Status);
        Assert.Equal(2, result.Resources.Count);
        LocalLimitResource weekly = result.Resources[0];
        Assert.Equal("stale", weekly.State);
        Assert.Equal("codex-app-server", weekly.Source);
        Assert.Equal("ProviderAuthoritative", weekly.Authority);
        Assert.Equal(42, weekly.UsedPercent);
        Assert.Equal(58, weekly.RemainingPercent);
        Assert.Equal("source-key-unverified", weekly.AccountIdentity);
        Assert.Null(result.Resources[1].UsedPercent);
        Assert.Equal("unavailable", result.Resources[1].State);
        Assert.DoesNotContain("private-account-key", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void DistinctSourceAccountKeys_NeverCollapseIntoOneResource()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-13T08:00:00Z");
        QuotaSnapshot first = new(QuotaWindowKind.Weekly, now, 20, 10080, now.AddDays(1),
            "codex", "default", "codex-app-server", "account-a") { LimitId = "weekly" };
        QuotaSnapshot second = first with { AccountKey = "account-b", UsedPercent = 70 };
        TelemetrySnapshot snapshot = TelemetrySnapshot.Empty with
        {
            CapturedAtUtc = now, QuotaSnapshots = [first, second], QuotaDataFresh = true,
        };

        LocalLimitsEnvelope result = LocalLimitsProjection.Build(snapshot, "local-secret", now);
        Assert.Equal(2, result.Resources.Count);
        Assert.NotEqual(result.Resources[0].Id, result.Resources[1].Id);
        Assert.Equal([20d, 70d], result.Resources.Select(resource => resource.UsedPercent));
    }
}
