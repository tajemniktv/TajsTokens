// Taj's Tokens | CodexDailyPairingTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class CodexDailyPairingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");

    private static CodexServerObservation Snapshot(bool relative, decimal? amount = null)
    {
        var row = new CodexDailyReportRow(
            "2026-09-18",
            relative ? null : amount,
            0,
            null,
            null,
            null,
            null,
            relative ? new Dictionary<string, decimal> { ["cli"] = amount ?? 100 } : null);
        return new CodexServerObservation(
            relative ? "relative" : "counts",
            relative ? CodexServerSurface.DailyRelativeUsage : CodexServerSurface.DailyCounts,
            null,
            Now,
            Now.AddSeconds(1),
            "test-contract",
            "test-client",
            ServerEvidenceState.Available,
            "")
        {
            AccountEvidence = AccountEvidenceClass.ServerCorrelated,
            CorrelatedAccountKey = "test-account",
            AccountBracket = new CodexServerAccountBracket("test-account", Now.AddSeconds(-1), "test-account", Now.AddSeconds(2)),
            DailyReport = new CodexDailyReport(
                "codex-private-daily/v1",
                relative ? "wham/usage/daily-token-usage-breakdown" : "wham/analytics/daily-workspace-usage-counts",
                "2026-09-01",
                "2026-09-19",
                relative ? "percent" : "credit",
                "day",
                null,
                "pro",
                "policy",
                "policy",
                [row]),
        };
    }

    [Fact]
    public void ConstructedRatioIsOnlyAHypothesisAndDoesNotClampRelativePercent()
    {
        CodexDailyPairingReport result = CodexDailyPairing.Evaluate([Snapshot(false, 9979.4m), Snapshot(true, 200)]);
        Assert.Empty(result.Exclusions);
        Assert.Equal(49.897m, Assert.Single(result.Days).CreditsPerPercentagePoint);
        Assert.Contains(result.Assumptions, x => x.Contains("denominator era is unknown"));
        Assert.Contains("Not a validated workload scale", result.ToDisplayText());
    }

    [Theory]
    [InlineData(null, "missing-native-credits")]
    [InlineData(0, "zero-native-credits-no-scale-evidence")]
    [InlineData(-1, "negative-native-credits")]
    public void MissingZeroAndNegativeCreditsDoNotEstablishScale(int? credits, string reason)
    {
        CodexDailyPair day = Assert.Single(CodexDailyPairing.Evaluate([Snapshot(false, credits), Snapshot(true)]).Days);
        Assert.Null(day.CreditsPerPercentagePoint);
        Assert.Contains(reason, day.Exclusions);
    }

    [Fact]
    public void LatestFailureSupersedesSuccessInsteadOfBackfillingAPair()
    {
        CodexServerObservation counts = Snapshot(false, 500);
        CodexServerObservation failed = counts with
        {
            Id = "failed", CollectedAtUtc = Now.AddMinutes(1), State = ServerEvidenceState.Error, DailyReport = null,
        };
        CodexDailyPairingReport result = CodexDailyPairing.Evaluate([counts, Snapshot(true), failed]);
        Assert.Equal("failed", result.CountsObservationId);
        Assert.Contains("latest-attempt-unavailable", result.Exclusions);
        Assert.Empty(result.Days);
    }

    [Fact]
    public void RevisedSnapshotsAreSelectedNotAdded()
    {
        CodexServerObservation counts = Snapshot(false, 500);
        CodexServerObservation old = counts with
        {
            Id = "old",
            CollectedAtUtc = Now.AddDays(-1),
            DailyReport = counts.DailyReport! with { Days = [counts.DailyReport.Days[0] with { Credits = 1000 }] },
        };
        Assert.Equal(5m, Assert.Single(CodexDailyPairing.Evaluate([old, counts, Snapshot(true)]).Days).CreditsPerPercentagePoint);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("bracket")]
    [InlineData("units")]
    [InlineData("policy")]
    [InlineData("freshness")]
    [InlineData("version")]
    [InlineData("range")]
    public void IncompatibleEvidenceNeverProducesRatios(string change)
    {
        CodexServerObservation relative = Snapshot(true);
        relative = change switch
        {
            "account" => relative with { CorrelatedAccountKey = "another" },
            "bracket" => relative with { AccountBracket = relative.AccountBracket! with { AfterCollectedAtUtc = Now } },
            "units" => relative with { DailyReport = relative.DailyReport! with { Units = "credits" } },
            "policy" => relative with { DailyReport = relative.DailyReport! with { PolicyAfter = "changed" } },
            "freshness" => relative with { DailyReport = relative.DailyReport! with { DataFreshness = "later" } },
            "range" => relative with { DailyReport = relative.DailyReport! with { StartDate = "2026-09-02" } },
            _ => relative with { ClientVersion = "changed" },
        };
        CodexDailyPairingReport report = CodexDailyPairing.Evaluate([Snapshot(false, 500), relative]);
        Assert.NotEmpty(report.Exclusions);
        Assert.Null(Assert.Single(report.Days).CreditsPerPercentagePoint);
    }

    [Fact]
    public void RangeDependentNormalizationRetainsContextRatherThanImplyingAStableQuotaRate()
    {
        CodexServerObservation counts = Snapshot(false, 100);
        CodexServerObservation relative = Snapshot(true, 40);
        CodexDailyPairingReport wide = CodexDailyPairing.Evaluate([counts, relative]);
        CodexDailyPairingReport narrow = CodexDailyPairing.Evaluate(
        [
            counts with { DailyReport = counts.DailyReport! with { StartDate = "2026-09-18" } },
            Snapshot(true, 100) with { DailyReport = Snapshot(true, 100).DailyReport! with { StartDate = "2026-09-18" } },
        ]);
        Assert.Equal(2.5m, Assert.Single(wide.Days).CreditsPerPercentagePoint);
        Assert.Equal(1m, Assert.Single(narrow.Days).CreditsPerPercentagePoint);
        Assert.Equal("2026-09-01", wide.RelativeRangeStart);
        Assert.Equal("2026-09-18", narrow.RelativeRangeStart);
        Assert.Equal("2026-09-19", narrow.RelativeRangeEnd);
        Assert.Contains("not allowance consumption", narrow.ToDisplayText());
        Assert.Contains("different ranges are not comparable", wide.ToDisplayText());
    }

    [Fact]
    public void SpillTinyUsageAndIncompleteDaysAreExcluded()
    {
        CodexServerObservation counts = Snapshot(false, 500);
        counts = counts with
        {
            DailyReport = counts.DailyReport! with
            {
                Days = [counts.DailyReport.Days[0] with { Date = "2026-09-19", OnDemandCredits = 1 }],
            },
        };
        CodexServerObservation relative = Snapshot(true, .01m);
        relative = relative with
        {
            DailyReport = relative.DailyReport! with { Days = [relative.DailyReport.Days[0] with { Date = "2026-09-19" }] },
        };
        CodexDailyPair day = Assert.Single(CodexDailyPairing.Evaluate([counts, relative]).Days);
        Assert.Null(day.CreditsPerPercentagePoint);
        Assert.Contains("credit-balance-spill-present", day.Exclusions);
        Assert.Contains("zero-or-tiny-relative-usage", day.Exclusions);
        Assert.Contains("incomplete-current-or-future-day", day.Exclusions);
        Assert.Contains("outside-common-range-interior", day.Exclusions);
    }
}