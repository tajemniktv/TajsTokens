using System.Text.Json;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public sealed record CodexPeriodReconciliation(string PeriodId, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    decimal? ReportedPercent, DateTimeOffset? DataAsOfUtc, bool? AccountingComplete,
    int Captures, int Revisions, long AttributedLocalTokens, double? LastCompatibleMeterPercent,
    IReadOnlyList<string> States, IReadOnlyList<string> Checks);

/// <summary>Revisable source comparisons only. Never supplies production training labels.</summary>
public static class CodexProviderReconciliation
{
    public const string Version = "codex-provider-reconciliation/v1";
    private static IEnumerable<CodexServerObservation> LatestAttempts(IEnumerable<CodexServerObservation> observations,
        CodexServerSurface surface)
    {
        var attempts = observations.Where(x => x.Surface == surface && x.ClientVersion == "codex-historical-analytics/v1")
            .OrderByDescending(x => x.CollectedAtUtc).ThenByDescending(x => x.Id).ToArray();
        // An uncorrelated failed bracket cannot establish which account remains selected.
        var barrier = attempts.FirstOrDefault(x => x.CorrelatedAccountKey is null &&
            x.State is not (ServerEvidenceState.Available or ServerEvidenceState.Empty))?.CollectedAtUtc;
        return attempts.Where(x => barrier is null || x.CollectedAtUtc > barrier)
            .GroupBy(x => x.CorrelatedAccountKey).Select(g => g.First());
    }

    public static IReadOnlyList<CodexChatAccounting> Tasks(IEnumerable<CodexServerObservation> observations,
        IReadOnlyList<CodexLedgerEntry> ledger, CodexSelection selection)
    {
        // Provider task totals have no project/model/date partition matching these local filters.
        if (selection.Model is not null || selection.Project is not null) return [];
        return LatestAttempts(observations, CodexServerSurface.TaskUsage)
            .Where(x => x.TaskUsage is not null && x.State is ServerEvidenceState.Available &&
                (selection.AccountKey is null || selection.AccountKey == x.CorrelatedAccountKey))
            .SelectMany(capture => capture.TaskUsage!.Threads
                .Where(task => selection.ThreadId is null || selection.ThreadId == task.ThreadId)
                .Select(task =>
                {
                    var local = ledger.Where(x => x.ThreadId == task.ThreadId).ToArray();
                    var owned = capture.CorrelatedAccountKey is not null && local.Length > 0 &&
                        local.All(x => x.AccountKey == capture.CorrelatedAccountKey);
                    return new CodexChatAccounting(task.ThreadId, local.Sum(x => x.Workload.ReportedTotalTokens),
                        capture.CorrelatedAccountKey, capture.CollectedAtUtc, capture.TaskUsage.DataAsOfUtc, task,
                        (local.Length == 0 ? "No local match in this range. " : owned ? "Exact thread ID and bounded account assertions match. " :
                            "Exact thread ID only; local account ownership incomplete or conflicting. ") +
                        "Local tokens cover the selection; task percentages are current-allowance estimates with unverified time/descendant coverage. No subtraction, allocation, coverage percentage or cross-task sum.");
                })).ToArray();
    }

    public static IReadOnlyList<CodexPeriodReconciliation> Analyze(IEnumerable<CodexServerObservation> observations,
        IReadOnlyList<CodexLedgerEntry> ledger, IReadOnlyList<QuotaSnapshot> quota, CodexSelection selection, DateTimeOffset now)
    {
        var result = new List<CodexPeriodReconciliation>();
        var retained = observations.ToArray();
        foreach (var newest in LatestAttempts(retained, CodexServerSurface.PlanHistory))
        {
            var accountKey = newest.CorrelatedAccountKey;
            var account = retained.Where(x => x.Surface == newest.Surface && x.ClientVersion == newest.ClientVersion && x.CorrelatedAccountKey == accountKey);
            if (newest.PlanHistory is null || newest.State is not (ServerEvidenceState.Available or ServerEvidenceState.Empty)) continue;
            var report = newest.PlanHistory;
            foreach (var period in report.Periods.Where(x => x.StartsAtUtc < selection.ToUtc && x.EndsAtUtc > selection.FromUtc))
            {
                var history = account.Where(x => x.PlanHistory is not null).OrderBy(x => x.CollectedAtUtc)
                    .Select(x => x.PlanHistory!.Periods.FirstOrDefault(p => p.Id == period.Id)).Where(x => x is not null).ToArray();
                var revisions = history.Zip(history.Skip(1)).Count(x => JsonSerializer.Serialize(x.First) != JsonSerializer.Serialize(x.Second));
                var states = new List<string> { "Shadow" };
                if (period.EndsAtUtc > now || period.AccountingComplete != true) states.Add("Provisional");
                if (period.AccountingComplete != true || report.CoverageComplete != true) states.Add("Incomplete");
                if (report.DataAsOfUtc is null) states.Add("Freshness unknown");
                else if (now - report.DataAsOfUtc > TimeSpan.FromHours(2)) states.Add("Delayed (>2h diagnostic threshold)");
                else states.Add("Recent as-of (not completeness)");
                if (revisions > 0) states.Add("Revised");
                if (history.Length > 1 && revisions == 0) states.Add("Unchanged across captures (not final)");
                if (report.Approximate) states.Add("Approximate boundaries");
                var checks = new List<string>();
                foreach (var group in period.Breakdowns)
                {
                    if (group.Rows.Select(x => x.Key).Distinct().Count() != group.Rows.Count)
                    { checks.Add($"{group.Dimension}: duplicate keys, conservation unavailable"); continue; }
                    if (period.UsedBasisPoints is { } used)
                    {
                        var difference = group.Rows.Sum(x => x.BasisPoints) - used;
                        checks.Add($"{group.Dimension}: partition minus period = {difference / 100m:0.####} pp; dimensions are alternatives, never added together");
                        if (Math.Abs(difference) > 1m) states.Add("Conflicting partition (>1 basis point diagnostic threshold)");
                    }
                }
                var scopeComplete = !selection.HasWorkFilter && selection.FromUtc <= period.StartsAtUtc && selection.ToUtc >= period.EndsAtUtc;
                if (!scopeComplete) checks.Add("Selection does not contain this whole unfiltered period; no whole-period comparison.");
                var tokens = ledger.Where(x => accountKey is not null && x.AccountKey == accountKey &&
                    x.ObservedAtUtc >= period.StartsAtUtc && x.ObservedAtUtc < period.EndsAtUtc).Sum(x => x.Workload.ReportedTotalTokens);
                checks.Add("Local tokens include only matching bounded account assertions; missing ownership is excluded, not estimated.");
                var tolerance = report.BoundaryToleranceSeconds ?? 0;
                var candidates = quota.Where(x => scopeComplete && accountKey is not null && x.AccountKey == accountKey &&
                    x.Authority == QuotaObservationAuthority.ProviderAuthoritative && x.PlanType == period.PlanType &&
                    x.WindowMinutes == period.WindowMinutes && x.ResetsAtUtc is { } reset &&
                    Math.Abs((reset - period.EndsAtUtc).TotalSeconds) <= tolerance &&
                    x.CapturedAtUtc >= period.StartsAtUtc && x.CapturedAtUtc < period.EndsAtUtc &&
                    report.DataAsOfUtc is { } asOf && x.CapturedAtUtc <= asOf).ToArray();
                var lanes = candidates.GroupBy(QuotaHistoryPolicy.Cohort).ToArray();
                var last = lanes.Length == 1 ? lanes[0].OrderByDescending(x => x.CapturedAtUtc).First() : null;
                checks.Add(last is null ? "No unique compatible live-meter lane with historical boundary/as-of support." :
                    $"Last compatible meter at {last.CapturedAtUtc:O}; not an integrated full-period total or proof of final agreement.");
                result.Add(new(period.Id, period.StartsAtUtc, period.EndsAtUtc, period.UsedBasisPoints / 100m,
                    report.DataAsOfUtc, period.AccountingComplete, history.Length, revisions, tokens, last?.UsedPercent,
                    states.Distinct().ToArray(), checks));
            }
        }
        return result;
    }
}
