// Taj's Tokens | CodexEvidenceDriftTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class CodexEvidenceDriftTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-19T10:00:00Z");

    private static CodexQuotaLimitMetadata Limit => new(
        "named:codex",
        "codex",
        "Codex",
        "pro",
        null,
        false,
        "model",
        new CodexQuotaCredits(true, false, "10"),
        new CodexSpendControlLimit("12.50", "0", 100, 1),
        new CodexQuotaReportedWindow(10, 300, 1),
        null);

    private static CodexServerObservation Quota(int index, CodexQuotaLimitMetadata? limit = null)
    {
        return new CodexServerObservation(
            index.ToString(),
            CodexServerSurface.QuotaMetadata,
            null,
            Start.AddMinutes(index),
            Start.AddMinutes(index).AddSeconds(1),
            "contract",
            "client",
            ServerEvidenceState.Available,
            "fixture")
        {
            CorrelatedAccountKey = "account",
            AccountEvidence = AccountEvidenceClass.ProviderVerified,
            QuotaMetadata = new CodexQuotaMetadataReport("quota/v1", [limit ?? Limit]),
        };
    }

    [Fact]
    public void OrdinaryConsumptionResetsBalanceAndDecimalFormattingAreNotPolicyDrift()
    {
        CodexQuotaLimitMetadata changed = Limit with
        {
            Primary = new CodexQuotaReportedWindow(90, 300, 999),
            Credits = new CodexQuotaCredits(false, false, "0"),
            IndividualLimit = new CodexSpendControlLimit("12.500", "12", 4, 99),
        };
        Assert.Empty(CodexEvidenceDrift.Analyze([Quota(0), Quota(1, changed)]));
    }

    [Fact]
    public void ConfigurationMissingnessAndBlockingStayDistinctWithStableProvenance()
    {
        CodexServerObservation before = Quota(0);
        CodexServerObservation after = Quota(
            1,
            Limit with
            {
                PlanType = "team", Primary = new CodexQuotaReportedWindow(0, 600, 2), NormalModelSlug = null, SpendControlReached = true,
            });
        IReadOnlyList<CodexDriftSignal> changes = CodexEvidenceDrift.Analyze([after, before, after]);
        Assert.Equal(4, changes.Count);
        Assert.Contains(changes, x => x.Kind == CodexDriftKind.ReportedConfiguration && x.Field.EndsWith("/plan"));
        Assert.Contains(changes, x => x.Kind == CodexDriftKind.Missingness && x.Field.EndsWith("/normal-model"));
        Assert.Contains(changes, x => x.Kind == CodexDriftKind.BlockingState);
        Assert.All(
            changes,
            x =>
            {
                Assert.Equal(before.Id, x.BeforeObservationId);
                Assert.Equal(after.Id, x.AfterObservationId);
                Assert.Equal(after.CollectedAtUtc, x.FirstObservedAtUtc);
                Assert.Null(x.EffectiveAtUtc);
            });
        Assert.Equal(changes, CodexEvidenceDrift.Analyze([before, after]));
    }

    [Fact]
    public void AccountSwitchFailureVersionChangeAndOverlappingFetchBreakComparison()
    {
        CodexServerObservation before = Quota(0);
        CodexServerObservation after = Quota(2, Limit with { PlanType = "changed" });
        Assert.Empty(CodexEvidenceDrift.Analyze([before, Quota(1) with { CorrelatedAccountKey = "other" }, after]));
        Assert.Empty(CodexEvidenceDrift.Analyze([before, Quota(1) with { AccountEvidence = AccountEvidenceClass.Unattributed }, after]));
        Assert.Empty(CodexEvidenceDrift.Analyze([before, after with { FetchStartedAtUtc = before.CollectedAtUtc }]));
        IReadOnlyList<CodexDriftSignal> failed = CodexEvidenceDrift.Analyze(
            [before, Quota(1) with { State = ServerEvidenceState.Error, QuotaMetadata = null }, after]);
        Assert.All(failed, x => Assert.Equal(CodexDriftKind.Capability, x.Kind));
        IReadOnlyList<CodexDriftSignal> upgraded = CodexEvidenceDrift.Analyze([before, after with { ClientVersion = "new-client" }]);
        Assert.Equal("client-version", Assert.Single(upgraded).Field);
    }

    [Fact]
    public void NamedViewReorderingIsNotChangeAndOmissionIsNotRevocation()
    {
        CodexQuotaLimitMetadata a = Limit;
        CodexQuotaLimitMetadata b = Limit with { ResponseKey = "legacy" };
        CodexServerObservation before = Quota(0) with { QuotaMetadata = new CodexQuotaMetadataReport("quota/v1", [a, b]) };
        Assert.Empty(
            CodexEvidenceDrift.Analyze([before, Quota(1) with { QuotaMetadata = new CodexQuotaMetadataReport("quota/v1", [b, a]) }]));
        CodexDriftSignal omitted = Assert.Single(CodexEvidenceDrift.Analyze([before, Quota(1)]));
        Assert.Equal(CodexDriftKind.Missingness, omitted.Kind);
        Assert.Equal("legacy/reported", omitted.Field);
    }

    [Fact]
    public void TinyPercentVariationDoesNotMasqueradeAsSubstantiveHistoricalRevision()
    {
        CodexDailyReport Report(decimal value, string units = "percent")
        {
            return new CodexDailyReport(
                "daily/v1",
                "relative",
                "2026-09-16",
                "2026-09-19",
                units,
                null,
                null,
                "pro",
                null,
                null,
                [
                    new CodexDailyReportRow(
                        "2026-09-17",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        new Dictionary<string, decimal> { ["desktop_app"] = value }),
                ]);
        }

        CodexServerObservation before = Quota(0) with
        {
            Surface = CodexServerSurface.DailyRelativeUsage, QuotaMetadata = null, DailyReport = Report(9.658874161270091m),
        };
        CodexServerObservation after = Quota(1) with
        {
            Surface = CodexServerSurface.DailyRelativeUsage, QuotaMetadata = null, DailyReport = Report(9.65887416127009m),
        };
        CodexDriftSignal signal = Assert.Single(CodexEvidenceDrift.Analyze([before, after]));
        Assert.Equal(CodexDriftKind.NumericalVariation, signal.Kind);
        Assert.NotEqual(signal.Before, signal.After); // Never round away the observed evidence.
        Assert.Equal(
            CodexDriftKind.HistoricalRevision,
            Assert.Single(
                CodexEvidenceDrift.Analyze(
                [
                    before,
                    after with { DailyReport = Report(9.659m) },
                ])).Kind);
        Assert.Equal(
            CodexDriftKind.HistoricalRevision,
            Assert.Single(
                CodexEvidenceDrift.Analyze(
                [
                    before with { DailyReport = Report(9.658874161270091m, "fraction") },
                    after with { DailyReport = Report(9.65887416127009m, "fraction") },
                ])).Kind);
        Assert.Empty(CodexEvidenceDrift.Analyze([before, after with { DailyReport = before.DailyReport }]));
        Assert.Empty(
            CodexEvidenceDrift.Analyze(
            [
                before, after with { DailyReport = Report(100m) with { StartDate = "2026-09-15" } },
            ])); // Same completed day can be renormalized by a different requested range.
        Assert.Empty(CodexEvidenceDrift.Analyze([before, after with { DailyReport = Report(100m) with { EndDate = "2026-09-20" } }]));
    }

    [Fact]
    public void CompletedInteriorDayOmissionsAreMissingnessNotZeroCostOrPolicyChanges()
    {
        CodexDailyReportRow Day(string date)
        {
            return new CodexDailyReportRow(date, 0, 0, 10, 0, 0, 10, null);
        }

        var report = new CodexDailyReport(
            "daily/v1",
            "counts",
            "2026-09-15",
            "2026-09-19",
            "credit",
            null,
            null,
            "pro",
            null,
            null,
            [Day("2026-09-15"), Day("2026-09-16"), Day("2026-09-18"), Day("2026-09-19")]);
        CodexServerObservation before = Quota(0) with
        {
            Surface = CodexServerSurface.DailyCounts, QuotaMetadata = null, DailyReport = report,
        };
        CodexServerObservation after = Quota(1) with
        {
            Surface = CodexServerSurface.DailyCounts,
            QuotaMetadata = null,
            DailyReport = report with { Days = [Day("2026-09-16"), Day("2026-09-17")] },
        };
        IReadOnlyList<CodexDriftSignal> signals = CodexEvidenceDrift.Analyze([before, after]);
        Assert.Equal(2, signals.Count);
        Assert.All(
            signals,
            x =>
            {
                Assert.Equal(CodexDriftKind.Missingness, x.Kind);
                Assert.Null(x.EffectiveAtUtc);
            });
        Assert.Contains(signals, x => x.Field == "2026-09-18/reported" && x.Before == "present" && x.After is null);
        Assert.Contains(signals, x => x.Field == "2026-09-17/reported" && x.Before is null && x.After == "present");
        // A shifted range, malformed range or duplicate dates cannot establish missing rows.
        Assert.Empty(
            CodexEvidenceDrift.Analyze([before, after with { DailyReport = after.DailyReport! with { StartDate = "2026-09-18" } }]));
        Assert.Empty(CodexEvidenceDrift.Analyze([before, after with { DailyReport = after.DailyReport! with { EndDate = "unknown" } }]));
        Assert.Empty(
            CodexEvidenceDrift.Analyze(
                [before, after with { DailyReport = after.DailyReport! with { Days = [Day("2026-09-16"), Day("2026-09-16")] } }]));
        Assert.DoesNotContain(
            CodexEvidenceDrift.Analyze([before, after with { DailyReport = after.DailyReport! with { Units = "USD" } }]),
            x => x.Field.EndsWith("/reported"));
        Assert.Equal(signals, CodexEvidenceDrift.Analyze([after, before, after]));
    }

    [Fact]
    public void OnlyComparableCompletedDaysBecomeRevisionSignals()
    {
        CodexDailyReportRow Day(string date, decimal credit)
        {
            return new CodexDailyReportRow(date, credit, 0, 10, 0, 0, 10, null);
        }

        var report = new CodexDailyReport(
            "daily/v1",
            "counts",
            "2026-09-17",
            "2026-09-19",
            "credit",
            null,
            "freshness-a",
            "pro",
            null,
            null,
            [Day("2026-09-17", 1.00m), Day("2026-09-18", 2), Day("2026-09-19", 1)]);
        CodexServerObservation before = Quota(0) with
        {
            Surface = CodexServerSurface.DailyCounts, QuotaMetadata = null, DailyReport = report,
        };
        CodexServerObservation after = Quota(1) with
        {
            Surface = CodexServerSurface.DailyCounts,
            QuotaMetadata = null,
            DailyReport = report with
            {
                DataFreshness = "freshness-b", Days = [Day("2026-09-17", 1m), Day("2026-09-18", 3), Day("2026-09-19", 8)],
            },
        };
        CodexDriftSignal revision = Assert.Single(CodexEvidenceDrift.Analyze([before, after]));
        Assert.Equal(CodexDriftKind.HistoricalRevision, revision.Kind);
        Assert.Equal("2026-09-18/credits", revision.Field);
        CodexDriftSignal unitChange = Assert.Single(
            CodexEvidenceDrift.Analyze([before, after with { DailyReport = after.DailyReport! with { Units = "USD" } }]));
        Assert.Equal("units", unitChange.Field);
        Assert.Empty(
            CodexEvidenceDrift.Analyze(
            [
                before with { DailyReport = report with { Units = null } },
                after with { DailyReport = after.DailyReport! with { Units = null } },
            ]));
    }
}