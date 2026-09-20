// Taj's Tokens | QuotaResetDetectorTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class QuotaResetDetectorTests
{
    private readonly QuotaResetDetector _detector = new();

    [Fact]
    public void AlternatingResetJitterProducesNoSignalsButLargerDriftDoes()
    {
        var reset = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        QuotaSnapshot[] rows = Enumerable.Range(0, 30).Select(i => Snapshot(
            reset.AddHours(-3).AddMinutes(i),
            20,
            reset.AddSeconds(i % 2))).ToArray();
        Assert.Empty(_detector.Detect(rows));
        QuotaSnapshot[] drift = rows.Take(3).Select((x, i) => x with { ResetsAtUtc = reset.AddSeconds(i) }).ToArray();
        Assert.Equal(QuotaResetClassification.ReanchoredWindow, Assert.Single(_detector.Detect(drift)).Classification);
        Assert.Equal(
            QuotaResetClassification.FullReset,
            Assert.Single(
                _detector.Detect(
                [
                    rows[0] with { UsedPercent = 90 }, rows[1] with { UsedPercent = 5 },
                ])).Classification);
    }

    [Fact]
    public void EventIdentityIncludesEveryCohortDimension()
    {
        var reset = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        QuotaSnapshot first = Snapshot(reset.AddHours(-3), 90, reset) with { Source = "codex-rollout", SessionId = "one" };
        QuotaSnapshot[] cohorts = new[]
        {
            first,
            first with { Provider = "other" },
            first with { Profile = "other" },
            first with { Kind = QuotaWindowKind.Weekly },
            first with { Source = "other-rollout" },
            first with { AccountKey = "account" },
            first with { LimitId = "bucket" },
            first with { PlanType = "plan" },
            first with { SessionId = "two" },
            first with { WindowMinutes = 301 },
        };
        IReadOnlyList<QuotaResetEvent> events = _detector.Detect(
            cohorts.SelectMany(x => new[] { x, x with { CapturedAtUtc = x.CapturedAtUtc.AddMinutes(1), UsedPercent = 5 } }).ToArray());
        Assert.Equal(cohorts.Length, events.Count);
        Assert.Equal(cohorts.Length, events.Select(x => x.EventId).Distinct().Count());
        Assert.All(events, x => Assert.StartsWith("quota-reset-v2-", x.EventId));
    }

    [Fact]
    public void Detect_MeterDropNearAuthoritativeBoundary_IsExpectedReset()
    {
        var boundary = new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
        QuotaSnapshot[] snapshots = new[]
        {
            Snapshot(boundary.AddMinutes(-5), 92, boundary), Snapshot(boundary.AddMinutes(2), 3, boundary.AddHours(5)),
        };

        QuotaResetEvent reset = Assert.Single(_detector.Detect(snapshots));

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
        QuotaSnapshot[] snapshots = new[]
        {
            Snapshot(oldReset.AddMinutes(-20), 17, oldReset),
            Snapshot(oldReset.AddMinutes(12), 17, oldReset.AddHours(5).AddMinutes(12)),
        };

        QuotaResetEvent reset = Assert.Single(_detector.Detect(snapshots));

        Assert.Equal(QuotaResetClassification.ReanchoredWindow, reset.Classification);
        Assert.True(reset.Confidence >= 0.9);
        Assert.Contains("re-anchor", reset.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_SmallDropWithIdentityChange_IsAmbiguousNotCleanReanchor()
    {
        var oldReset = new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
        QuotaSnapshot[] snapshots = new[]
        {
            Snapshot(oldReset.AddHours(-2), 17, oldReset), Snapshot(oldReset.AddHours(-1), 14, oldReset.AddHours(5)),
        };

        QuotaResetEvent reset = Assert.Single(_detector.Detect(snapshots));

        Assert.Equal(QuotaResetClassification.UnusualReset, reset.Classification);
        Assert.Equal(0.55, reset.Confidence, 3);
        Assert.Contains("ambiguous", reset.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_SameIdentityMaterialDrop_IsLowerConfidenceUnusualEvidence()
    {
        var resetAt = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        QuotaSnapshot[] snapshots = new[] { Snapshot(resetAt.AddHours(-2), 66, resetAt), Snapshot(resetAt.AddHours(-1), 48, resetAt) };

        QuotaResetEvent reset = Assert.Single(_detector.Detect(snapshots));

        Assert.Equal(QuotaResetClassification.UnusualReset, reset.Classification);
        Assert.Equal(0.5, reset.Confidence, 3);
    }

    [Fact]
    public void Detect_OrdinaryIncreasingUsage_ProducesNoResetEvent()
    {
        DateTimeOffset resetAt = DateTimeOffset.UtcNow.AddHours(3);
        QuotaSnapshot[] snapshots = new[]
        {
            Snapshot(resetAt.AddHours(-4), 12, resetAt),
            Snapshot(resetAt.AddHours(-3), 18, resetAt),
            Snapshot(resetAt.AddHours(-2), 31, resetAt),
        };

        Assert.Empty(_detector.Detect(snapshots));
    }

    [Fact]
    public void Detect_IsStableAndProviderProfileScoped()
    {
        var boundary = new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
        QuotaSnapshot[] snapshots = new[]
        {
            Snapshot(boundary.AddMinutes(-3), 80, boundary, "codex", "a"),
            Snapshot(boundary.AddMinutes(1), 2, boundary.AddHours(5), "codex", "a"),
            Snapshot(boundary.AddMinutes(-3), 50, boundary, "codex", "b"),
            Snapshot(boundary.AddMinutes(1), 1, boundary.AddHours(5), "codex", "b"),
        };

        IReadOnlyList<QuotaResetEvent> first = _detector.Detect(snapshots);
        IReadOnlyList<QuotaResetEvent> second = _detector.Detect(snapshots);

        Assert.Equal(2, first.Count);
        Assert.Equal(first.Select(item => item.EventId), second.Select(item => item.EventId));
        Assert.Equal(2, first.Select(item => item.Profile).Distinct().Count());
    }

    [Fact]
    public void Detect_SameObservationPairKeepsIdentityWhenInterpretationChanges()
    {
        var boundary = new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
        QuotaSnapshot previous = Snapshot(boundary.AddHours(-2), 80, boundary);
        QuotaSnapshot currentWithoutBoundaryMatch = Snapshot(boundary.AddHours(-1), 74, boundary.AddHours(5));
        QuotaSnapshot currentNearBoundary = Snapshot(boundary.AddMinutes(2), 74, boundary.AddHours(5));

        QuotaResetEvent unusual = Assert.Single(_detector.Detect(new[] { previous, currentWithoutBoundaryMatch }));
        QuotaSnapshot correctedPrevious = Snapshot(boundary.AddMinutes(-3), 80, boundary);
        QuotaResetEvent expected = Assert.Single(_detector.Detect(new[] { correctedPrevious, currentNearBoundary }));

        // Stability is tested for a fixed observation pair below; classification/reset metadata are
        // deliberately absent from the identity material so corrections update rather than fork.
        QuotaResetEvent first = Assert.Single(
            _detector.Detect(
                new[] { Snapshot(boundary.AddMinutes(-3), 80, boundary), Snapshot(boundary.AddMinutes(2), 74, boundary.AddHours(5)) }));
        QuotaResetEvent second = Assert.Single(
            _detector.Detect(
                new[]
                {
                    Snapshot(boundary.AddMinutes(-3), 80, boundary.AddMinutes(1)),
                    Snapshot(boundary.AddMinutes(2), 74, boundary.AddHours(5).AddMinutes(1)),
                }));

        Assert.Equal(QuotaResetClassification.UnusualReset, unusual.Classification);
        Assert.Equal(QuotaResetClassification.ExpectedReset, expected.Classification);
        Assert.Equal(first.EventId, second.EventId);
    }

    private static QuotaSnapshot Snapshot(
        DateTimeOffset captured,
        double used,
        DateTimeOffset reset,
        string provider = "codex",
        string profile = "default")
    {
        return new QuotaSnapshot(QuotaWindowKind.FiveHour, captured, used, 300, reset, provider, profile, "fixture");
    }
}