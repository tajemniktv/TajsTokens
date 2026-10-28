using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>One presentation projection for Overview, Planning and tray. No model fitting or source reads.</summary>
public static class CodexProductProjection
{
    public const string Version = "codex-product-status/v1";
    public static CodexMeasuredWork RetainedWork(TelemetrySnapshot telemetry, WorkloadReferenceState? reference)
    {
        var native = telemetry.TokenUsages.Where(x => x.Provider == "codex-native").ToArray();
        bool fallback = telemetry.TokenGeneration?.Quality == TelemetryDataQuality.Fallback;
        bool available = !fallback && (native.Length > 0 || telemetry.TokenGeneration?.Source == "Native Codex");
        decimal Total(TokenBreakdown b) => b.ReportedTotal ??
            (decimal)b.UncachedInput + b.CacheRead + b.CacheWrite + b.NonReasoningOutput + b.ReasoningOutput;
        decimal? total = available ? native.Sum(x => Total(x.Breakdown)) : null;
        TtScoreAssessment? tt = null;
        if (available && reference?.Selection is { } selected)
        {
            var rows = native.GroupBy(x => x.Model, StringComparer.Ordinal).Select(group =>
            {
                var amounts = group.Select(x => x.Breakdown).ToArray();
                var model = string.IsNullOrWhiteSpace(group.Key) || group.Key == "(unknown)" ? null : group.Key;
                return new TtQuantityConstraints(new(model, null), amounts.Sum(x => (decimal)x.UncachedInput),
                    amounts.Sum(x => (decimal)x.CacheRead), amounts.Sum(x => (decimal)x.CacheWrite),
                    amounts.Sum(x => (decimal)x.NonReasoningOutput), amounts.Sum(x => (decimal)x.ReasoningOutput),
                    ReportedTotal: amounts.Sum(Total));
            }).OrderBy(x => x.Coordinate!.Model, StringComparer.Ordinal).ToArray();
            var retained = rows.Take(CodexJointWorkload.MaximumCoordinates).ToList();
            if (rows.Length > retained.Count)
            {
                var overflow = rows.Skip(retained.Count).ToArray();
                retained.Add(new(null, overflow.Sum(x => x.UncachedInput), overflow.Sum(x => x.CacheRead),
                    overflow.Sum(x => x.CacheWrite), overflow.Sum(x => x.NonReasoningOutput), overflow.Sum(x => x.ReasoningOutput),
                    ReportedTotal: overflow.Sum(x => x.ReportedTotal)));
            }
            tt = TtJointScoring.Evaluate(selected.Basis, retained);
        }
        return new(total, tt, reference?.Selection?.Basis.BasisId, reference?.Selection?.Basis.Vintage,
            "All retained local work", available ? "Native local" : fallback ? "Fallback · native unavailable" : "Native unavailable",
            null, telemetry.TokenGeneration?.AsOfUtc, false, fallback,
            "Retained model/category aggregates, including undated amounts. Effort is unavailable in this compact projection; an effort-dependent basis can remain partial. " +
            (telemetry.TokenGeneration?.Coverage ?? "Local collection coverage unknown."));
    }

    public static CodexMeasuredWork WithFreshness(CodexMeasuredWork work, TelemetrySnapshot telemetry, DateTimeOffset now)
    {
        var source = telemetry.Sources.FirstOrDefault(x => x.Provider == telemetry.TokenGeneration?.Source);
        bool fresh = telemetry.TokenDataFresh && telemetry.TokenGeneration?.State == TelemetryHealthState.Live &&
            source?.LastSuccessUtc is { } success && success <= now && now - success <= TimeSpan.FromHours(6);
        return work with { IsFresh = fresh, ReadAtUtc = source?.LastSuccessUtc };
    }

    public static CodexProductStatus Build(CodexIntelligenceSnapshot snapshot, DateTimeOffset now)
    {
        var work = snapshot.RetainedWork ?? new(null, null, null, null, "All retained local work", "Native unavailable", null, null, false, false, "No retained projection.");
        if (snapshot.DatasetCapturedAtUtc is { } read)
            work = new(snapshot.JointWorkload?.ReportedTokens ?? snapshot.Ledger.Sum(x => (decimal)x.Workload.ReportedTotalTokens),
                snapshot.Reference?.Tt, snapshot.Reference?.Sources.Selection?.Basis.BasisId, snapshot.Reference?.Sources.Selection?.Basis.Vintage,
                $"Selected history · {snapshot.Selection.FromUtc:yyyy-MM-dd}–{snapshot.Selection.ToUtc.AddTicks(-1):yyyy-MM-dd} UTC",
                "Local evidence", read, snapshot.Ledger.Select(x => x.CollectedAtUtc).Max(),
                snapshot.RetainedWork?.IsFresh == true, false, "Selected local increments; account associations and filters do not allocate provider quota.");
        return new(now, QuotaOutlookProjection.Build(snapshot, now), work, snapshot.StatusChanges) { LocalNowcast = snapshot.Workload };
    }

    public static string Tokens(decimal? value) => value switch
    {
        null => "tokens unavailable", >= 1_000_000_000 => $"{value / 1_000_000_000:0.##}B tokens",
        >= 1_000_000 => $"{value / 1_000_000:0.##}M tokens", >= 1000 => $"{value / 1000:0.##}K tokens", _ => $"{value:N0} tokens",
    };
    public static string Tt(TtScoreAssessment? tt) => tt?.State switch
    {
        TtScoreState.Exact => $"{tt.ExactTt:N2} TT", TtScoreState.FiniteBounds => $"{tt.LowerTt:N2}–{tt.UpperTt:N2} TT · bounds",
        TtScoreState.Partial => $"{tt.CoveredSubtotalTt:N2} TT · covered portion", TtScoreState.Inconsistent => "TT unavailable · totals differ",
        TtScoreState.ArithmeticUnavailable => "TT unavailable · numeric limit", TtScoreState.Unbounded => "TT has no finite upper bound", _ => "TT unavailable",
    };
    public static string WorkLabel(CodexMeasuredWork work) => $"{work.Source} · {(work.IsFresh ? "fresh collection" : "collection stale/unknown")}" +
        (work.Vintage is null ? " · no selected TT basis" : $" · frozen {work.Vintage}");
    public static string CompactOutlook(AllowanceOutlook window) => !window.Supported ? window.Conclusion :
        window.Exhausted ? "Exhausted" : window.Reading?.Forecast?.SurvivesUntilReset == false && window.Reading.Forecast.EstimatedExhaustionAtUtc is { } eta
            ? $"At pace, exhausted {eta.ToLocalTime():ddd HH:mm}"
            : $"At pace, ~{window.Reading?.Forecast?.ProjectedRemainingAtResetPercent:0.#}% at reset";
    public static string RangeLabel(AllowanceOutlook window) => window.Exhausted ? "provider reported" : !window.Supported ? window.Status : window.Reading?.Forecast?.Evidence is
        { RemainingAtResetLowerPercent: { }, RemainingAtResetUpperPercent: { } } && window.Supported
            ? "empirical reset range" : "reset range uncalibrated";

    public static string PaceLabel(AllowanceOutlook window) => window.Supported && !window.Exhausted &&
        window.Reading?.Forecast is { BurnRatePercentPerHour: { } pace, SustainablePercentPerHour: { } budget }
            ? $"{pace:0.#} pp/h · sustainable {budget:0.#} pp/h · {RangeLabel(window)}" : RangeLabel(window);

    public static string NowcastSummary(CodexProductStatus status)
    {
        if (status.LocalNowcast?.IsStale == true) return "Local workload telemetry is stale; no active nowcast.";
        var prediction = status.LocalNowcast?.Predictions.OrderBy(x => Math.Abs(x.HorizonHours - .25)).FirstOrDefault();
        if (prediction is null || !double.IsFinite(prediction.ExpectedTokens) || prediction.ExpectedTokens < 0 ||
            prediction.ExpectedTokens >= (double)decimal.MaxValue || !double.IsFinite(prediction.HorizonHours) || prediction.HorizonHours <= 0)
            return "Local workload nowcast is not available for this scope.";
        return $"Local workload · ~{Tokens((decimal)prediction.ExpectedTokens)} over the next {prediction.HorizonHours * 60:g} minutes. Separate from quota cost.";
    }

    public static string Details(CodexProductStatus status) => string.Join("\n\n", status.Allowances.Windows.Select(x =>
        $"{QuotaOutlookProjection.Name(x.Kind)} · {QuotaAccountScope.Describe(x.Reading?.Current.AccountKey)}\n{x.Detail}\n" +
        (x.Reading?.Forecast?.Evidence?.PaceReceipt?.Describe() ?? "No calculation receipt.") + "\n" +
        x.Reading?.HistoryPolicy + "\n" + x.Reading?.Diagnostic + "\n" +
        string.Join("\n", (x.Reading?.Forecast?.Evidence?.HorizonPredictions ?? []).Select(h =>
            $"At {h.TargetUtc.ToLocalTime():g}: {h.ExpectedUsagePercent:0.##} pp · {h.Model} · {h.ValidationSamples} validation samples · {h.Explanation}")))) +
        $"\n\n{status.Work.Scope}: {Tokens(status.Work.NativeTokens)} · {Tt(status.Work.Tt)}\n{WorkLabel(status.Work)}\n" +
        $"Projection read {status.Work.ReadAtUtc?.ToString("O") ?? "unknown"}; last token collection {status.Work.LastCollectionUtc?.ToString("O") ?? "unknown"}.\n" +
        status.Work.Coverage + "\n" + (status.Work.Tt?.Explanation ?? "TT support unavailable.") +
        (status.Work.Tt is { } tt ? $"\nTT quantity constraints: {tt.ConstraintRows} rows; {tt.UnmappedRows} unmapped / {tt.InconsistentRows} inconsistent. These are quantity rows, not source-record counts." : "") +
        "\n\n" + string.Join("\n", status.Changes.Select(x => $"{x.ObservedAtUtc.ToLocalTime():g} · {x.Summary}\n{x.Detail}")) +
        (status.LocalNowcast is { } local ? $"\n\nLocal nowcast · generated {local.GeneratedAtUtc:O} · {(local.IsStale ? "stale" : "conditional")}\n" +
            string.Join("\n", local.Predictions.Select(x => $"{x.HorizonHours * 60:g} min: {x.ExpectedTokens:N0} tokens · {x.Explanation}")) : "");

    public static IReadOnlyList<CodexStatusChange> Changes(CodexProductStatus? before, CodexProductStatus after)
    {
        if (before is null) return [];
        var changes = new List<CodexStatusChange>();
        void Add(string code, string summary, string detail, string? account = null) => changes.Add(new(code, summary, detail, after.AsOfUtc, account));
        if (before.Work.NativeTokens != after.Work.NativeTokens)
            Add("local-retained-work", "Retained local work updated", $"{Tokens(before.Work.NativeTokens)} → {Tokens(after.Work.NativeTokens)}. Ingestion or revisions can change retained totals; this is not account quota consumption.");
        if (before.Work.BasisId != after.Work.BasisId)
            Add("tt-basis", "Selected TT basis changed", $"{before.Work.BasisId ?? "unavailable"} → {after.Work.BasisId ?? "unavailable"}. Original saved scores are unchanged.");
        else if (before.Work.Tt != after.Work.Tt)
            Add("tt-reference", "Retained TT reference updated", "The same basis was reapplied to retained quantities or labels. Coverage can change without a native-token change; original saved scores are unchanged.");
        if (before.Work.IsFresh != after.Work.IsFresh || before.Work.Source != after.Work.Source)
            Add("local-freshness", "Local source status changed", WorkLabel(after.Work));
        foreach (var next in after.Allowances.Windows)
        {
            var prior = before.Allowances.Windows.FirstOrDefault(x => x.Kind == next.Kind);
            var old = prior?.Reading?.Current; var current = next.Reading?.Current;
            string name = QuotaOutlookProjection.Name(next.Kind);
            if (old is not null && current is not null &&
                (QuotaHistoryPolicy.Cohort(old) != QuotaHistoryPolicy.Cohort(current) || old.ResetsAtUtc != current.ResetsAtUtc))
                Add("meter-scope", $"{name} meter scope/reset changed", "Readings belong to a new account/source/plan/window or reset; a percentage difference is not treated as consumption.", current.AccountKey);
            else if (old?.UsedPercent != current?.UsedPercent)
                Add("provider-meter", $"{name} provider reading changed", $"Reported remaining {old?.RemainingPercent:0.#}% → {current?.RemainingPercent:0.#}%. Rounded readings do not measure exact consumption.", current?.AccountKey);
            if (prior?.Status != next.Status || prior?.NotReported != next.NotReported)
                Add("quota-support", $"{name} status: {next.Status}", next.Detail, current?.AccountKey ?? old?.AccountKey);
            var oldForecast = prior?.Reading?.Forecast; var newForecast = next.Reading?.Forecast;
            if (oldForecast?.BurnRatePercentPerHour != newForecast?.BurnRatePercentPerHour || oldForecast?.Evidence?.Model != newForecast?.Evidence?.Model ||
                oldForecast?.EstimatedExhaustionAtUtc != newForecast?.EstimatedExhaustionAtUtc ||
                oldForecast?.ProjectedRemainingAtResetPercent != newForecast?.ProjectedRemainingAtResetPercent ||
                oldForecast?.SustainablePercentPerHour != newForecast?.SustainablePercentPerHour)
                Add("pace-selection", $"{name} pace recalculated", $"Anchor {current?.CapturedAtUtc:O}. " +
                    (newForecast?.Evidence?.PaceReceipt?.SelectionReason ?? next.Detail), current?.AccountKey);
        }
        return changes.AsReadOnly();
    }
}
