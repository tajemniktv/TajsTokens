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
