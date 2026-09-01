using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaResetDetectorTests
{
    private readonly QuotaResetDetector _detector = new();

    [Fact]
    public void Detect_MeterDropNearAuthoritativeBoundary_IsExpectedReset()
    {
        var boundary = new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
        var snapshots = new[]
        {
            Snapshot(boundary.AddMinutes(-5), 92, boundary),
            Snapshot(boundary.AddMinutes(2), 3, boundary.AddHours(5))
        };

        var reset = Assert.Single(_detector.Detect(snapshots));

        Assert.Equal(QuotaResetClassification.ExpectedReset, reset.Classification);
        Assert.Equal(boundary, reset.EffectiveAtUtc);
        Assert.True(reset.Confidence >= 0.88);
        Assert.Equal(92, reset.BeforeUsedPercent);
        Assert.Equal(3, reset.AfterUsedPercent);
    }

    [Fact]
    public void Detect_ExpiredWindowAdvancesWithoutVisibleDrop_IsReanchorNotPhantomReset()
    {
        var oldReset = new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
        var snapshots = new[]
        {
            Snapshot(oldReset.AddMinutes(-20), 17, oldReset),
            Snapshot(oldReset.AddMinutes(12), 17, oldReset.AddHours(5).AddMinutes(12))
        };

        var reset = Assert.Single(_detector.Detect(snapshots));

        Assert.Equal(QuotaResetClassification.ReanchoredWindow, reset.Classification);
        Assert.True(reset.Confidence >= 0.9);
        Assert.Contains("re-anchor", reset.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_SameIdentityMaterialDrop_IsLowerConfidenceUnusualEvidence()
    {
        var resetAt = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var snapshots = new[]
        {
            Snapshot(resetAt.AddHours(-2), 66, resetAt),
            Snapshot(resetAt.AddHours(-1), 48, resetAt)
        };

        var reset = Assert.Single(_detector.Detect(snapshots));

        Assert.Equal(QuotaResetClassification.UnusualReset, reset.Classification);
        Assert.Equal(0.5, reset.Confidence, 3);
    }

    [Fact]
    public void Detect_OrdinaryIncreasingUsage_ProducesNoResetEvent()
    {
        var resetAt = DateTimeOffset.UtcNow.AddHours(3);
        var snapshots = new[]
        {
            Snapshot(resetAt.AddHours(-4), 12, resetAt),
            Snapshot(resetAt.AddHours(-3), 18, resetAt),
            Snapshot(resetAt.AddHours(-2), 31, resetAt)
        };

        Assert.Empty(_detector.Detect(snapshots));
    }

    [Fact]
    public void Detect_IsStableAndProviderProfileScoped()
    {
        var boundary = new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
        var snapshots = new[]
        {
            Snapshot(boundary.AddMinutes(-3), 80, boundary, provider: "codex", profile: "a"),
            Snapshot(boundary.AddMinutes(1), 2, boundary.AddHours(5), provider: "codex", profile: "a"),
            Snapshot(boundary.AddMinutes(-3), 50, boundary, provider: "codex", profile: "b"),
            Snapshot(boundary.AddMinutes(1), 1, boundary.AddHours(5), provider: "codex", profile: "b")
        };

        var first = _detector.Detect(snapshots);
        var second = _detector.Detect(snapshots);

        Assert.Equal(2, first.Count);
        Assert.Equal(first.Select(item => item.EventId), second.Select(item => item.EventId));
        Assert.Equal(2, first.Select(item => item.Profile).Distinct().Count());
    }

    private static QuotaSnapshot Snapshot(
        DateTimeOffset captured,
        double used,
        DateTimeOffset reset,
        string provider = "codex",
        string profile = "default") =>
        new(QuotaWindowKind.FiveHour, captured, used, 300, reset, provider, profile, "fixture");
}
