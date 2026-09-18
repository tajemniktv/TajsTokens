using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public sealed record QuotaHistoryDecision(QuotaSnapshot Observation, QuotaHistoryCohort Cohort,
    bool Eligible, string Reason);

/// <summary>Rebuildable historical eligibility, not acquisition-time authority or account attribution.</summary>
public static class QuotaHistoryPolicy
{
    public const string Version = "quota-history/v1";

    public static QuotaHistoryCohort Cohort(QuotaSnapshot row) => new(row.Provider, row.Profile,
        row.Kind, row.Source, row.AccountKey,
        row.LimitId ?? (row.Source == "codex-app-server:codex" || row.ObservationId is not null &&
            row.Authority == QuotaObservationAuthority.EmbeddedObservation ? "codex" : null),
        row.PlanType, row.Authority == QuotaObservationAuthority.EmbeddedObservation ? row.SessionId : null, row.WindowMinutes);

    public static IReadOnlyList<QuotaHistoryDecision> Describe(IEnumerable<QuotaSnapshot> observations,
        DateTimeOffset asOf)
    {
        var result = new List<QuotaHistoryDecision>();
        foreach (var group in observations.GroupBy(Cohort))
        {
            QuotaSnapshot? previous = null;
            foreach (var atTime in group.OrderBy(x => x.CapturedAtUtc).GroupBy(x => x.CapturedAtUtc))
            {
                var conflicts = atTime.Select(x => (x.UsedPercent, x.WindowMinutes, x.ResetsAtUtc)).Distinct().Count() > 1;
                var first = true;
                foreach (var row in atTime)
                {
                    var embedded = row.Authority == QuotaObservationAuthority.EmbeddedObservation;
                    var reason = row.Provider != "codex" || row.Authority == QuotaObservationAuthority.Unknown ? "unsupported-source"
                        : row.Kind == QuotaWindowKind.Unknown ? "unsupported-window"
                        : row.CapturedAtUtc > asOf ? "future-event"
                        : !row.HasSourceTimestamp ? "missing-event-time"
                        : row.UsedPercent is not { } used || !double.IsFinite(used) || used is < 0 or > 100 ||
                            row.WindowMinutes is not > 0 || row.ResetsAtUtc is null || row.ResetsAtUtc < row.CapturedAtUtc ? "invalid-window"
                        : embedded && (row.ObservationId is null || row.SourceIdentity is null || row.SessionId is null) ? "legacy-missing-provenance"
                        : conflicts ? "same-time-conflict"
                        : !first ? "same-time-repeat"
                        : embedded && previous is not null && row.UsedPercent == previous.UsedPercent &&
                            row.ResetsAtUtc == previous.ResetsAtUtc && row.WindowMinutes == previous.WindowMinutes ? "possibly-cached-repeat"
                        : embedded ? "historical-embedded-account-unverified" : "historical-app-server";
                    var eligible = reason.StartsWith("historical-", StringComparison.Ordinal);
                    result.Add(new(row, group.Key, eligible, reason));
                    if (eligible) previous = row;
                    // A conflict/invalid row is a boundary, never silently bridge through it.
                    else if (reason is not "possibly-cached-repeat" and not "same-time-repeat") previous = null;
                    first = false;
                }
            }
        }
        return result;
    }

    public static string Summarize(IReadOnlyList<QuotaHistoryDecision> rows) =>
        $"{Version}: {rows.Count(x => x.Eligible)} eligible of {rows.Count} observations; " +
        string.Join("; ", rows.GroupBy(x => x.Reason).OrderBy(x => x.Key).Select(x => $"{ReasonLabel(x.Key)}: {x.Count()}")) +
        ". Sources, reported plans/buckets and unknown-account rollout sessions remain separate; cached readings are not independent targets.";

    private static string ReasonLabel(string reason) => reason switch
    {
        "historical-app-server" => "Eligible app-server readings",
        "historical-embedded-account-unverified" => "Eligible rollout readings (account unverified)",
        "possibly-cached-repeat" => "Possibly cached rollout repeats withheld",
        "same-time-repeat" => "Duplicate readings withheld",
        "same-time-conflict" => "Conflicting readings withheld",
        "legacy-missing-provenance" => "Legacy rows without native provenance withheld",
        "invalid-window" => "Missing/invalid meter or reset",
        "future-event" => "Future-dated readings withheld",
        "missing-event-time" => "Missing source event time",
        "unsupported-window" => "Unsupported window durations retained",
        _ => "Unsupported sources"
    };

    public static string DescribeCohort(QuotaHistoryCohort? cohort) => cohort is null ? "legacy cohort" :
        $"bucket {cohort.LimitId ?? "unknown"} · plan {cohort.PlanType ?? "unknown"}" +
        (cohort.SessionId is not null ? $" · rollout session {cohort.SessionId}" : "");

    public static IEnumerable<IGrouping<QuotaHistoryCohort, QuotaHistoryDecision>> Streams(
        IReadOnlyList<QuotaHistoryDecision> rows) => rows
        .Where(x => x.Eligible || x.Reason is "same-time-conflict" or "invalid-window" or "missing-event-time")
        .GroupBy(x => x.Cohort).Where(x => x.Any(d => d.Eligible));

    public static QuotaSnapshot[] ReplayRows(IEnumerable<QuotaHistoryDecision> rows) => rows
        .Select(x => x.Eligible ? x.Observation : x.Observation with { UsedPercent = null })
        .OrderBy(x => x.CapturedAtUtc).ToArray();

    public static QuotaSnapshot[] AvailableRows(IEnumerable<QuotaSnapshot> rows, ForecastReplayAvailability availability) =>
        (availability == ForecastReplayAvailability.ReconstructedEventTime ? rows : rows
            .Where(x => x.CollectedAtUtc is not null || x.Authority == QuotaObservationAuthority.ProviderAuthoritative)
            .Select(x => x.CollectedAtUtc is { } collected && collected > x.CapturedAtUtc
                ? x with { CapturedAtUtc = collected } : x))
        .OrderBy(x => x.CapturedAtUtc).ToArray();
}
