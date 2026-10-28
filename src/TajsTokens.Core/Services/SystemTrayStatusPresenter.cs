// Taj's Tokens | SystemTrayStatusPresenter.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

public static class SystemTrayStatusPresenter
{
    public static SystemTrayStatus WithRecentFleet(SystemTrayStatus status, int recentlyWorkingThreads)
    {
        if (recentlyWorkingThreads <= 0) return status;
        string quota = status.Tooltip.StartsWith("TajsTokens · ", StringComparison.Ordinal)
            ? status.Tooltip["TajsTokens · ".Length..]
            : status.Tooltip;
        return status with { Tooltip = $"TajsTokens · {quota} · {recentlyWorkingThreads} recent turn(s)" };
    }

    public static SystemTrayStatus Build(CodexIntelligenceSnapshot snapshot)
    {
        var product = CodexProductProjection.Build(snapshot, DateTimeOffset.UtcNow);
        bool multiple = snapshot.Current.Select(x => x.Current.AccountKey).Distinct().Count() > 1;
        var remaining = product.Allowances.Windows.Where(x => !x.NotReported && x.Reading?.Current.UsedPercent is >= 0 and <= 100)
            .Select(x => x.Reading!.Current.RemainingPercent!.Value).ToArray();
        var rows = product.Allowances.Windows.Select(x =>
        {
            string value = x.NotReported ? "not reported" : x.Reading?.Current.UsedPercent is >= 0 and <= 100
                ? $"{x.Reading.Current.RemainingPercent:0.#}% left" : "unavailable";
            return $"{QuotaOutlookProjection.Name(x.Kind)}: {value} · {CodexProductProjection.CompactOutlook(x)}";
        }).ToList();
        rows.Add(product.Allowances.Summary);
        rows.Add($"{product.Work.Scope}: {CodexProductProjection.Tokens(product.Work.NativeTokens)} · {CodexProductProjection.Tt(product.Work.Tt)}");
        rows.Add(CodexProductProjection.WorkLabel(product.Work));
        if (product.Changes.Count > 0) rows.Add("Changed: " + product.Changes[0].Summary);
        string quota = multiple ? "multiple accounts" : string.Join(" · ", product.Allowances.Windows.Select(x =>
            (x.Kind == QuotaWindowKind.FiveHour ? "5h " : "week ") + (x.NotReported ? "not reported" :
                x.Reading?.Current.UsedPercent is >= 0 and <= 100 ? $"{x.Reading.Current.RemainingPercent:0}%" : "?")));
        string runway = product.Allowances.Limiting is { } limiting ? (limiting.Exhausted ? "provider reported · " : "conditional · ") + CodexProductProjection.CompactOutlook(limiting)
            : "runway unavailable";
        bool Fresh(AllowanceOutlook x) =>
            x.Reading is { IsFresh: true } r && r.Current.CapturedAtUtc <= product.AsOfUtc &&
            product.AsOfUtc - r.Current.CapturedAtUtc <= TimeSpan.FromHours(6);
        bool fresh = !multiple && product.Allowances.Windows.All(x => x.NotReported || Fresh(x));
        string health = multiple ? "account scope" : fresh ? "live" : product.Allowances.Windows.Any(Fresh) ? "partial"
            : snapshot.Current.Count > 0 ? "stale" : "quota unavailable";
        return new SystemTrayStatus("", null, fresh, "")
        {
            Tooltip = $"TajsTokens · {quota} · {health}\n{runway}\nLocal retained · {CodexProductProjection.Tt(product.Work.Tt)}",
            IsFresh = fresh, HealthLabel = health,
            ConstrainedRemainingPercent = multiple || remaining.Length == 0 ? null : (int)Math.Round(remaining.Min(), MidpointRounding.AwayFromZero),
            SummaryRows = rows.AsReadOnly(),
        };
    }

    public static SystemTrayStatus Build(TelemetrySnapshot snapshot)
    {
        return Build(CodexIntelligenceProjection.Live(snapshot));
    }

    public static SystemTrayStatus Build(CodexCurrentState snapshot)
    {
        return Build(TelemetrySnapshot.Empty with { CapturedAtUtc = snapshot.CapturedAtUtc,
            QuotaSnapshots = snapshot.QuotaSnapshots, QuotaLanes = snapshot.QuotaLanes,
            QuotaDataFresh = snapshot.QuotaDataFresh, Sources = snapshot.Sources });
    }
}
