using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexProductProjectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static WorkloadReferenceState References(bool effort = false, decimal multiplier = 1)
    {
        var dims = TtJointDimensions.Model | (effort ? TtJointDimensions.ReasoningEffort : 0);
        var a = new CodexWorkloadCoordinate("A", effort ? "high" : null);
        var b = new CodexWorkloadCoordinate("B", effort ? "high" : null);
        var basis = new TtJointWorkloadBasis(TtJointWorkloadBasis.Version, dims, "fixture only", "relative", "fixture",
            ["fixture"], [new(a, TtTokenCategory.UncachedInput, multiplier), new(a, TtTokenCategory.NonReasoningOutput, 3 * multiplier),
                new(b, TtTokenCategory.UncachedInput, 10 * multiplier), new(b, TtTokenCategory.NonReasoningOutput, 30 * multiplier)],
            [new(a, TtTokenCategory.UncachedInput, 1000)]);
        return new(new(Now, "fixture", basis), null);
    }
    private static TelemetrySnapshot Telemetry(bool fresh = true) => TelemetrySnapshot.Empty with
    {
        CapturedAtUtc = Now, TokenDataFresh = fresh,
        TokenUsages = [new("codex-native", "codex", "A", null, new(1000, 0, 0, 0, 0, 1000)),
            new("codex-native", "codex", "B", null, new(0, 0, 0, 1000, 0, 1000))],
        TokenGeneration = new("Native Codex", fresh ? TelemetryHealthState.Live : TelemetryHealthState.Stale,
            TelemetryDataQuality.Primary, "local only", Now.AddDays(-1), 1),
        Sources = [new("Native Codex", fresh ? TelemetryHealthState.Live : TelemetryHealthState.Stale, "local", Now)],
    };
    private static CurrentQuotaForecast Reading(string account = "account")
    {
        var q = new QuotaSnapshot(QuotaWindowKind.Weekly, Now, 60, 10080, Now.AddDays(3), "codex", "default", "codex-app-server", account);
        var forecast = new Forecast(q.Kind, Now, 2, Now.AddHours(20), false, 1, 0, ForecastState.ExhaustionLikelyBeforeReset);
        return new(q, TelemetryHealthState.Live, forecast, "fixture");
    }
    private static CodexIntelligenceSnapshot Snapshot(params CurrentQuotaForecast[] readings)
    {
        var telemetry = Telemetry() with { QuotaSnapshots = readings.Select(x => x.Current).ToArray(), CurrentForecasts = readings, QuotaDataFresh = true };
        return CodexIntelligenceProjection.Live(telemetry) with
            { RetainedWork = CodexProductProjection.WithFreshness(CodexProductProjection.RetainedWork(telemetry, References()), telemetry, Now) };
    }

    [Fact]
    public void CompactNativeAggregatesPreserveModelCategoryPairingWithoutInventingEffortOrDates()
    {
        var work = CodexProductProjection.RetainedWork(Telemetry(), References());
        Assert.Equal(2000, work.NativeTokens); Assert.Equal(31, work.Tt!.ExactTt);
        Assert.Equal(CodexJointQuantityKind.ObservedIncrements, work.Tt.Kind);
        Assert.Contains("including undated", work.Coverage);
        var effort = CodexProductProjection.RetainedWork(Telemetry(), References(effort: true));
        Assert.Equal(2000, effort.NativeTokens); Assert.Equal(TtScoreState.Partial, effort.Tt!.State);
        Assert.Null(effort.Tt.ExactTt); Assert.Equal(2000, effort.Tt.KnownUnmappedTokens);
    }

    [Fact]
    public void NativeUnavailableFallbackAndVerifiedEmptyRemainDistinct()
    {
        Assert.Null(CodexProductProjection.RetainedWork(TelemetrySnapshot.Empty, References()).NativeTokens);
        var fallback = Telemetry() with { TokenGeneration = Telemetry().TokenGeneration! with { Quality = TelemetryDataQuality.Fallback, Source = "Tokscale fallback" } };
        var result = CodexProductProjection.RetainedWork(fallback, References());
        Assert.True(result.IsFallback); Assert.Null(result.NativeTokens); Assert.Null(result.Tt);
        var empty = CodexProductProjection.RetainedWork(Telemetry() with { TokenUsages = [] }, References());
        Assert.Equal(0, empty.NativeTokens); Assert.Equal(0, empty.Tt!.ExactTt);
    }

    [Fact]
    public void NativeReportedTotalsSurviveCategoryMismatchWhileWholeReferenceScoreIsWithheld()
    {
        var data = Telemetry();
        data = data with { TokenUsages = [data.TokenUsages[0] with { Breakdown = new(1000, 0, 0, 0, 0, 1100) }, data.TokenUsages[1]] };
        var work = CodexProductProjection.RetainedWork(data, References());
        Assert.Equal(2100, work.NativeTokens);
        Assert.Equal(TtScoreState.Inconsistent, work.Tt!.State); Assert.Null(work.Tt.ExactTt);
        Assert.Contains("totals differ", CodexProductProjection.Tt(work.Tt));
        Assert.Equal(1, work.Tt.InconsistentRows);
    }

    [Fact]
    public void FreshnessAgesBySourceScanClockWithoutRewritingRetainedWorkOrUsingLastTokenAge()
    {
        var data = Telemetry(); var work = CodexProductProjection.RetainedWork(data, References());
        Assert.True(CodexProductProjection.WithFreshness(work, data, Now).IsFresh);
        var stale = CodexProductProjection.WithFreshness(work, data, Now.AddHours(7));
        Assert.False(stale.IsFresh); Assert.Equal(31, stale.Tt!.ExactTt); Assert.Equal(2000, stale.NativeTokens);
        Assert.False(CodexProductProjection.WithFreshness(work, data with { Sources = [] }, Now).IsFresh);
    }

    [Fact]
    public void SelectedHistoryAndRetainedTotalsKeepTheirOwnScopeWithoutAllocatingQuotaToFilters()
    {
        var live = Snapshot(Reading());
        var row = new CodexPredictiveTokenEvent("s", Now.AddDays(-2), Now.AddDays(-1), "A", "high", 1000, 0, 0, 0, 0, 1000);
        var joint = CodexJointWorkloadBuilder.Build([row]);
        var selected = live with { Selection = new(Now.AddDays(-3), Now.AddDays(-1), Model: "A", Project: "p"),
            DatasetCapturedAtUtc = Now, JointWorkload = joint, Reference = WorkloadReferenceProjection.BuildConditional(References(), joint, Now) };
        var frame = CodexProductProjection.Build(selected, Now);
        Assert.Equal(1000, frame.Work.NativeTokens); Assert.Equal(1, frame.Work.Tt!.ExactTt);
        Assert.Contains("Selected history", frame.Work.Scope);
        Assert.Equal(CodexProductProjection.Build(live, Now).Allowances.Summary, frame.Allowances.Summary);
        Assert.Equal(2000, selected.RetainedWork!.NativeTokens);
        var missingAccount = CodexIntelligenceProjection.WithLiveContext(selected, live, "missing");
        Assert.Empty(missingAccount.Current);
        Assert.Equal(1000, CodexProductProjection.Build(missingAccount, Now).Work.NativeTokens);
        Assert.Equal(2000, missingAccount.RetainedWork!.NativeTokens);
    }

    [Fact]
    public void StatusChangesDistinguishScopeAndResetChangesFromProviderMovementAndLocalRevisions()
    {
        var before = CodexProductProjection.Build(Snapshot(Reading()), Now);
        var changed = Reading() with { Current = Reading().Current with { UsedPercent = 61 } };
        var after = CodexProductProjection.Build(Snapshot(changed), Now.AddMinutes(1));
        Assert.Contains(CodexProductProjection.Changes(before, after), x => x.Code == "provider-meter");
        var reset = changed with { Current = changed.Current with { ResetsAtUtc = Now.AddDays(4) } };
        var scopeChanges = CodexProductProjection.Changes(before, CodexProductProjection.Build(Snapshot(reset), Now));
        Assert.Contains(scopeChanges, x => x.Code == "meter-scope");
        Assert.DoesNotContain(scopeChanges, x => x.Code == "provider-meter");
        var revised = after with { Work = after.Work with { NativeTokens = 1900 } };
        Assert.Contains(CodexProductProjection.Changes(after, revised), x => x.Code == "local-retained-work" && x.Detail.Contains("revisions"));
        Assert.Empty(CodexProductProjection.Changes(before, before with { AsOfUtc = Now.AddMinutes(1) }));
    }

    [Fact]
    public void CurrentProductUsesOnlyInMemoryInputsAndReusesTtUntilQuantitiesOrBasisChange()
    {
        var data = Telemetry(); var reference = References(); int reads = 0;
        var engine = new CodexIntelligenceEngine(() => { reads++; throw new InvalidOperationException("Must not read history"); },
            null!, null!, () => data, currentReferences: () => reference);
        var first = engine.Current; var repeated = engine.Current;
        Assert.Equal(reference.Selection!.Basis.BasisId, first.Manifest.Components["tt"]);
        Assert.Same(first.Reference!.Tt, first.RetainedWork!.Tt);
        Assert.Same(first.RetainedWork!.Tt, repeated.RetainedWork!.Tt); Assert.Equal(0, reads);
        reference = References(effort: true);
        Assert.Equal(TtScoreState.Partial, engine.Current.RetainedWork!.Tt!.State);
        Assert.Contains(engine.Current.StatusChanges, x => x.Code == "tt-basis");
        data = data with { TokenUsages = [data.TokenUsages[0]] };
        Assert.Equal(1000, engine.Current.RetainedWork!.NativeTokens);
        Assert.Contains(engine.Current.StatusChanges, x => x.Code == "local-retained-work");
        Assert.Equal(0, reads);
    }

    [Fact]
    public void LabelRepairAndChangedRunwayHaveReasonsEvenWhenTokenCountOrPaceAreUnchanged()
    {
        var before = CodexProductProjection.Build(Snapshot(Reading()), Now);
        var data = Telemetry(); data = data with { TokenUsages = [data.TokenUsages[0] with { Model = "B" }, data.TokenUsages[1]] };
        var repaired = before with { Work = CodexProductProjection.RetainedWork(data, References()) };
        Assert.Equal(before.Work.NativeTokens, repaired.Work.NativeTokens);
        Assert.NotEqual(before.Work.Tt!.ExactTt, repaired.Work.Tt!.ExactTt);
        Assert.Contains(CodexProductProjection.Changes(before, repaired), x => x.Code == "tt-reference");
        var reading = Reading(); reading = reading with { Forecast = reading.Forecast! with { EstimatedExhaustionAtUtc = Now.AddHours(19) } };
        Assert.Contains(CodexProductProjection.Changes(before, CodexProductProjection.Build(Snapshot(reading), Now)), x => x.Code == "pace-selection");
    }

    [Fact]
    public void TraySharesConditionalRunwayMeasuredTotalsAndInspectableReasonsWithoutFalseSafety()
    {
        var snapshot = Snapshot(Reading());
        var tray = SystemTrayStatusPresenter.Build(snapshot);
        Assert.Contains("conditional", tray.Tooltip);
        Assert.Contains(tray.SummaryRows, x => x.Contains("2K tokens") && x.Contains("31"));
        Assert.Contains(tray.SummaryRows, x => x == CodexProductProjection.Build(snapshot, Now).Allowances.Summary);
        var stale = snapshot with { Current = [Reading() with { State = TelemetryHealthState.Stale }] };
        Assert.False(SystemTrayStatusPresenter.Build(stale).IsFresh);
        Assert.DoesNotContain("conditional", SystemTrayStatusPresenter.Build(stale).Tooltip);
        var invalid = snapshot with { Current = [Reading() with { Current = Reading().Current with { UsedPercent = double.NaN } }] };
        Assert.Null(SystemTrayStatusPresenter.Build(invalid).ConstrainedRemainingPercent);
    }

    [Fact]
    public void MultipleAccountsCannotBorrowWindowNumbersOrRunwayAndAccountReasonsAreFiltered()
    {
        var snapshot = Snapshot(Reading("first"), Reading("second")) with { StatusChanges = [
            new("provider-meter", "first", "first-detail", Now, "first"), new("provider-meter", "second", "second-detail", Now, "second"),
            new("local-retained-work", "local", "local-detail", Now)] };
        var product = CodexProductProjection.Build(snapshot, Now);
        Assert.All(product.Allowances.Windows, x => Assert.Null(x.Reading));
        Assert.Null(SystemTrayStatusPresenter.Build(snapshot).ConstrainedRemainingPercent);
        var selected = CodexIntelligenceProjection.WithLiveContext(snapshot, snapshot, "second");
        Assert.DoesNotContain(selected.StatusChanges, x => x.AccountKey == "first");
        Assert.Equal(2, selected.StatusChanges.Count);
    }

    [Fact]
    public void FutureOrMismatchedForecastCannotProduceASupportedSharedRunway()
    {
        var reading = Reading();
        var future = reading with { Forecast = reading.Forecast! with { GeneratedAtUtc = Now.AddMinutes(1) } };
        Assert.False(CodexProductProjection.Build(Snapshot(future), Now).Allowances.Windows.Single(x => x.Kind == reading.Current.Kind).Supported);
        var changedAnchor = reading with { Current = reading.Current with { CapturedAtUtc = Now.AddTicks(1) } };
        Assert.False(CodexProductProjection.Build(Snapshot(changedAnchor), Now.AddMinutes(1)).Allowances.Windows.Single(x => x.Kind == reading.Current.Kind).Supported);
    }
}
