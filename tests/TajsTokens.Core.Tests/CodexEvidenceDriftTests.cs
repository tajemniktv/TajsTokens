using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexEvidenceDriftTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-19T10:00:00Z");
    private static CodexQuotaLimitMetadata Limit => new("named:codex", "codex", "Codex", "pro", null, false,
        "model", new(true, false, "10"), new("12.50", "0", 100, 1), new(10, 300, 1), null);
    private static CodexServerObservation Quota(int index, CodexQuotaLimitMetadata? limit = null) =>
        new(index.ToString(), CodexServerSurface.QuotaMetadata, null, Start.AddMinutes(index), Start.AddMinutes(index).AddSeconds(1),
            "contract", "client", ServerEvidenceState.Available, "fixture")
        {
            CorrelatedAccountKey = "account", AccountEvidence = AccountEvidenceClass.ProviderVerified,
            QuotaMetadata = new("quota/v1", [limit ?? Limit])
        };

    [Fact]
    public void OrdinaryConsumptionResetsBalanceAndDecimalFormattingAreNotPolicyDrift()
    {
        var changed = Limit with { Primary = new(90, 300, 999), Credits = new(false, false, "0"),
            IndividualLimit = new("12.500", "12", 4, 99) };
        Assert.Empty(CodexEvidenceDrift.Analyze([Quota(0), Quota(1, changed)]));
    }

    [Fact]
    public void ConfigurationMissingnessAndBlockingStayDistinctWithStableProvenance()
    {
        var before = Quota(0);
        var after = Quota(1, Limit with { PlanType = "team", Primary = new(0, 600, 2),
            NormalModelSlug = null, SpendControlReached = true });
        var changes = CodexEvidenceDrift.Analyze([after, before, after]);
        Assert.Equal(4, changes.Count);
        Assert.Contains(changes, x => x.Kind == CodexDriftKind.ReportedConfiguration && x.Field.EndsWith("/plan"));
        Assert.Contains(changes, x => x.Kind == CodexDriftKind.Missingness && x.Field.EndsWith("/normal-model"));
        Assert.Contains(changes, x => x.Kind == CodexDriftKind.BlockingState);
        Assert.All(changes, x =>
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
        var before = Quota(0);
        var after = Quota(2, Limit with { PlanType = "changed" });
        Assert.Empty(CodexEvidenceDrift.Analyze([before, Quota(1) with { CorrelatedAccountKey = "other" }, after]));
        Assert.Empty(CodexEvidenceDrift.Analyze([before, Quota(1) with { AccountEvidence = AccountEvidenceClass.Unattributed }, after]));
        Assert.Empty(CodexEvidenceDrift.Analyze([before, after with { FetchStartedAtUtc = before.CollectedAtUtc }]));
        var failed = CodexEvidenceDrift.Analyze([before, Quota(1) with { State = ServerEvidenceState.Error, QuotaMetadata = null }, after]);
        Assert.All(failed, x => Assert.Equal(CodexDriftKind.Capability, x.Kind));
        var upgraded = CodexEvidenceDrift.Analyze([before, after with { ClientVersion = "new-client" }]);
        Assert.Equal("client-version", Assert.Single(upgraded).Field);
    }

    [Fact]
    public void NamedViewReorderingIsNotChangeAndOmissionIsNotRevocation()
    {
        var a = Limit;
        var b = Limit with { ResponseKey = "legacy" };
        var before = Quota(0) with { QuotaMetadata = new("quota/v1", [a, b]) };
        Assert.Empty(CodexEvidenceDrift.Analyze([before, Quota(1) with { QuotaMetadata = new("quota/v1", [b, a]) }]));
        var omitted = Assert.Single(CodexEvidenceDrift.Analyze([before, Quota(1)]));
        Assert.Equal(CodexDriftKind.Missingness, omitted.Kind);
        Assert.Equal("legacy/reported", omitted.Field);
    }

    [Fact]
    public void TinyPercentVariationDoesNotMasqueradeAsSubstantiveHistoricalRevision()
    {
        CodexDailyReport Report(decimal value, string units = "percent") => new("daily/v1", "relative", "2026-09-16", "2026-09-19", units, null,
            null, "pro", null, null, [new("2026-09-17", null, null, null, null, null, null, new Dictionary<string, decimal> { ["desktop_app"] = value })]);
        var before = Quota(0) with { Surface = CodexServerSurface.DailyRelativeUsage, QuotaMetadata = null, DailyReport = Report(9.658874161270091m) };
        var after = Quota(1) with { Surface = CodexServerSurface.DailyRelativeUsage, QuotaMetadata = null, DailyReport = Report(9.65887416127009m) };
        var signal = Assert.Single(CodexEvidenceDrift.Analyze([before, after]));
        Assert.Equal(CodexDriftKind.NumericalVariation, signal.Kind);
        Assert.NotEqual(signal.Before, signal.After); // Never round away the observed evidence.
        Assert.Equal(CodexDriftKind.HistoricalRevision, Assert.Single(CodexEvidenceDrift.Analyze([before,
            after with { DailyReport = Report(9.659m) }])).Kind);
        Assert.Equal(CodexDriftKind.HistoricalRevision, Assert.Single(CodexEvidenceDrift.Analyze([
            before with { DailyReport = Report(9.658874161270091m, "fraction") },
            after with { DailyReport = Report(9.65887416127009m, "fraction") }])).Kind);
        Assert.Empty(CodexEvidenceDrift.Analyze([before, after with { DailyReport = before.DailyReport }]));
        Assert.Empty(CodexEvidenceDrift.Analyze([before, after with
        {
            DailyReport = Report(100m) with { StartDate = "2026-09-15" }
        }])); // Same completed day can be renormalized by a different requested range.
        Assert.Empty(CodexEvidenceDrift.Analyze([before, after with
        {
            DailyReport = Report(100m) with { EndDate = "2026-09-20" }
        }]));
    }

    [Fact]
    public void CompletedInteriorDayOmissionsAreMissingnessNotZeroCostOrPolicyChanges()
    {
        CodexDailyReportRow Day(string date) => new(date, 0, 0, 10, 0, 0, 10, null);
        var report = new CodexDailyReport("daily/v1", "counts", "2026-09-15", "2026-09-19", "credit", null,
            null, "pro", null, null, [Day("2026-09-15"), Day("2026-09-16"), Day("2026-09-18"), Day("2026-09-19")]);
        var before = Quota(0) with { Surface = CodexServerSurface.DailyCounts, QuotaMetadata = null, DailyReport = report };
        var after = Quota(1) with { Surface = CodexServerSurface.DailyCounts, QuotaMetadata = null,
            DailyReport = report with { Days = [Day("2026-09-16"), Day("2026-09-17")] } };
        var signals = CodexEvidenceDrift.Analyze([before, after]);
        Assert.Equal(2, signals.Count);
        Assert.All(signals, x => { Assert.Equal(CodexDriftKind.Missingness, x.Kind); Assert.Null(x.EffectiveAtUtc); });
        Assert.Contains(signals, x => x.Field == "2026-09-18/reported" && x.Before == "present" && x.After is null);
        Assert.Contains(signals, x => x.Field == "2026-09-17/reported" && x.Before is null && x.After == "present");
        // A shifted range, malformed range or duplicate dates cannot establish missing rows.
        Assert.Empty(CodexEvidenceDrift.Analyze([before, after with { DailyReport = after.DailyReport! with { StartDate = "2026-09-18" } }]));
        Assert.Empty(CodexEvidenceDrift.Analyze([before, after with { DailyReport = after.DailyReport! with { EndDate = "unknown" } }]));
        Assert.Empty(CodexEvidenceDrift.Analyze([before, after with { DailyReport = after.DailyReport! with { Days = [Day("2026-09-16"), Day("2026-09-16")] } }]));
        Assert.DoesNotContain(CodexEvidenceDrift.Analyze([before, after with { DailyReport = after.DailyReport! with { Units = "USD" } }]), x => x.Field.EndsWith("/reported"));
        Assert.Equal(signals, CodexEvidenceDrift.Analyze([after, before, after]));
    }

    [Fact]
    public void OnlyComparableCompletedDaysBecomeRevisionSignals()
    {
        CodexDailyReportRow Day(string date, decimal credit) => new(date, credit, 0, 10, 0, 0, 10, null);
        var report = new CodexDailyReport("daily/v1", "counts", "2026-09-17", "2026-09-19", "credit", null,
            "freshness-a", "pro", null, null, [Day("2026-09-17", 1.00m), Day("2026-09-18", 2), Day("2026-09-19", 1)]);
        var before = Quota(0) with { Surface = CodexServerSurface.DailyCounts, QuotaMetadata = null, DailyReport = report };
        var after = Quota(1) with { Surface = CodexServerSurface.DailyCounts, QuotaMetadata = null,
            DailyReport = report with { DataFreshness = "freshness-b", Days = [Day("2026-09-17", 1m), Day("2026-09-18", 3), Day("2026-09-19", 8)] } };
        var revision = Assert.Single(CodexEvidenceDrift.Analyze([before, after]));
        Assert.Equal(CodexDriftKind.HistoricalRevision, revision.Kind);
        Assert.Equal("2026-09-18/credits", revision.Field);
        var unitChange = Assert.Single(CodexEvidenceDrift.Analyze([before, after with { DailyReport = after.DailyReport! with { Units = "USD" } }]));
        Assert.Equal("units", unitChange.Field);
        Assert.Empty(CodexEvidenceDrift.Analyze([before with { DailyReport = report with { Units = null } },
            after with { DailyReport = after.DailyReport! with { Units = null } }]));
    }
}
