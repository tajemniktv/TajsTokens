using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

internal static class ForecastJournalCli
{
    public static async Task RunAsync(string[] args)
    {
        DateTimeOffset from = DateTimeOffset.Parse(args[2], CultureInfo.InvariantCulture).ToUniversalTime();
        DateTimeOffset to = DateTimeOffset.Parse(args[3], CultureInfo.InvariantCulture).ToUniversalTime();
        if (from >= to || to > DateTimeOffset.UtcNow || to - from > TimeSpan.FromDays(366))
            throw new ArgumentException("Choose an ordered past UTC range of at most 366 days.");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var repository = new SqliteTelemetryRepository(args[1]);
        var entries = new List<QuotaForecastComputationEntry>();
        DateTimeOffset? after = null; string? id = null;
        bool truncated = false;
        while (true)
        {
            var page = await repository.ReadForecastComputationsAsync("codex", "default", from, to, 1000,
                cancellation.Token, after, id);
            if (page.Count == 0) break;
            if (entries.Count == 10_000) { truncated = true; break; }
            entries.AddRange(page);
            if (page[^1].RecordedAtUtc is null) throw new InvalidOperationException("Cannot paginate an invalid recording clock.");
            after = page[^1].RecordedAtUtc; id = page[^1].Id;
            if (page.Count < 1000) break;
        }
        if (args[0] is "--forecast-score" or "--forecast-score-audit")
        {
            var computations = entries.Where(e => e.Problem is null && e.Computation is not null).Select(e => e.Computation!).ToArray();
            IReadOnlyList<QuotaSnapshot> quota = [];
            if (computations.Any(c => c.AnchorUtc < to))
            {
                DateTimeOffset firstAnchor = computations.Where(c => c.AnchorUtc < to).Min(c => c.AnchorUtc);
                // Include the preceding reported window so a bounded reset-jitter chain or
                // returning plan regime is not accidentally restarted at the report boundary.
                double windowMinutes = computations.Max(c => (double)Math.Clamp(c.Cohort.WindowMinutes ?? 10080, 1, 527040));
                DateTimeOffset contextFrom = firstAnchor - DateTimeOffset.MinValue > TimeSpan.FromMinutes(windowMinutes)
                    ? firstAnchor.AddMinutes(-windowMinutes) : DateTimeOffset.MinValue;
                var data = await new SqliteForecastDatasetReader(args[1]).ReadQuotaOnlyAsync("codex", "default",
                    contextFrom, to, cancellation.Token, accountKey: null);
                quota = data.Quota;
            }
            var report = RecordedQuotaForecastEvaluation.Evaluate(entries, quota, to, cancellation.Token,
                includeTrials: args[0] == "--forecast-score-audit");
            Console.WriteLine(JsonSerializer.Serialize(new { JournalTruncated = truncated, Report = report },
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        // Alias private cohorts. No raw IDs, receipts, paths, native records or arbitrary payloads leave the report.
        var cohorts = entries.Where(e => e.Computation is not null).Select(e => e.Computation!.Cohort)
            .Distinct().Select((cohort, index) => (cohort, alias: "cohort-" + (index + 1)))
            .ToDictionary(x => x.cohort, x => x.alias);
        var rows = entries.Where(e => e.Computation is not null).SelectMany(e => e.Computation!.Points.Select(p => new
            { Entry = e, Point = p, Cohort = cohorts[e.Computation.Cohort] })).ToArray();
        var groups = rows.GroupBy(x => new { x.Cohort, x.Entry.Computation!.Phase, x.Point.Role, x.Point.HorizonHours, x.Point.Model })
            .Select(group => new
            {
                group.Key.Cohort, Phase = group.Key.Phase.ToString(), Role = group.Key.Role.ToString(),
                group.Key.HorizonHours, group.Key.Model, Computations = group.Count(),
                SupportedPoints = group.Count(x => x.Point.RemainingPercent is not null),
                RecordedBeforeTarget = group.Count(x => x.Entry.Timing(x.Point) == QuotaForecastRecordingTiming.BeforeTarget),
                SupportedBeforeTarget = group.Count(x => x.Point.RemainingPercent is not null && x.Entry.Timing(x.Point) == QuotaForecastRecordingTiming.BeforeTarget),
                RecordedAtOrAfterTarget = group.Count(x => x.Entry.Timing(x.Point) == QuotaForecastRecordingTiming.AtOrAfterTarget),
                InvalidClocks = group.Count(x => x.Entry.Timing(x.Point) == QuotaForecastRecordingTiming.InvalidClock),
                Unconfirmed = group.Count(x => x.Entry.Timing(x.Point) == QuotaForecastRecordingTiming.Unconfirmed),
                EarliestRecordUtc = group.Min(x => x.Entry.RecordedAtUtc), LatestRecordUtc = group.Max(x => x.Entry.RecordedAtUtc),
                MinimumIssueLeadMinutes = group.Min(x => (x.Point.TargetUtc - x.Entry.ConfirmedAtUtc)?.TotalMinutes),
                MaximumGenerationDelaySeconds = group.Max(x => (x.Entry.ConfirmedAtUtc - x.Entry.Computation!.GeneratedAtUtc)?.TotalSeconds),
            }).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Contract = QuotaForecastComputation.Version, FromUtc = from, ToUtc = to, Recordings = entries.Count,
            Truncated = truncated, CorruptOrUnsupported = entries.Count(e => e.Problem is not null),
            Scope = "Computed outputs recorded by the forecast owner; not proof of dashboard publication or sent warnings. Before-target recording is separate from timely nominal-origin issuance. No outcome scoring, model selection or probability claim.",
            Groups = groups,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
