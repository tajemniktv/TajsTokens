using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Services;

internal static class SharedWindowsCli
{
    public static async Task RunAsync(string[] args)
    {
        DateTimeOffset Parse(string v) => DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
        var from = Parse(args[2]); var to = Parse(args[3]);
        if (from >= to || to > DateTimeOffset.UtcNow || to - from > TimeSpan.FromDays(366)) throw new ArgumentException("Choose a past UTC range up to 366 days.");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var raw = await new SqliteForecastDatasetReader(args[1]).ReadAsync("codex", "default", from, to, cancellation.Token);
        bool strict = args[0].Contains("strict", StringComparison.Ordinal), audit = args[0].EndsWith("-audit", StringComparison.Ordinal);
        var availability = strict ? ForecastReplayAvailability.CollectedByOrigin : ForecastReplayAvailability.ReconstructedEventTime;
        var quota = QuotaHistoryPolicy.AvailableRows(raw.Quota, availability).Where(q => q.CapturedAtUtc <= to)
            .Select(q => strict && (q.CollectedAtUtc is null || q.CollectedAtUtc < (q.ReplayMeasuredAtUtc ?? q.CapturedAtUtc) ||
                q.CollectedAtUtc > (q.ReplayMeasuredAtUtc ?? q.CapturedAtUtc).AddMinutes(5)) ? q with { UsedPercent = null } : q).ToArray();
        var data = raw with { Quota = quota, CapturedAtUtc = to };
        var built = QuotaCostObservationBuilder.BuildDetailed(data, cancellation.Token, horizons: ComposedQuotaValidation.EvaluationHorizons);
        var comparison = QuotaTransferEvaluator.EvaluateAligned(built.Observations, from, to, availability, cancellation.Token, audit);
        var cost = QuotaCostEvaluation.Evaluate(built.Observations, "Same retained ordinary frame; no source acquisition or TT changes.", cancellation.Token);
        var regime = CodexRegimeModel.Analyze(cost, cancellation.Token);
        var cohorts = cost.Scores.Select(s => s.Cohort).Distinct().ToList();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            DatasetIdentity = ForecastThresholdStudy.Fingerprint(data), Comparison = comparison,
            Marker = CodexRegimeMarkerProjection.Build(new(from, to), cost, regime, ForecastThresholdStudy.Fingerprint(data)),
            FrozenReference = cost.Version, RegimeVersion = regime.Version, RegimeMethod = regime.Methodology,
            KnownPlanGroups = built.Observations.Select(r => r.Cohort.PlanType).Where(p => p is not null).Distinct().Count(),
            RequestedTierRows = raw.Workload.Count(w => w.ServiceTier is not null),
            RequestedTierLabels = raw.Workload.Select(w => w.ServiceTier).Where(t => t is not null).Distinct().Count(),
            QualityCounts = built.Observations.SelectMany(r => r.QualityFlags).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count()),
            Candidates = regime.Candidates.Select(c => new { Cohort = "reference-" + (cohorts.IndexOf(c.Cohort) + 1),
                Window = c.Cohort.Kind.ToString(), c.HorizonHours, c.BoundaryLowerUtc, c.BoundaryUpperUtc,
                c.BeforeResidual, c.AfterResidual, c.RelativeRate, c.BeforeSamples, c.AfterSamples, c.Explanation }),
            References = cost.Scores.Where(s => s.Candidate is "categories" or "total" or "time-ablation").Select(s => new
            { Cohort = "reference-" + (cohorts.IndexOf(s.Cohort) + 1), Window = s.Cohort.Kind.ToString(), KnownAccount = s.Cohort.AccountKey is not null,
                s.HorizonHours, s.Candidate, s.TrainingSamples, s.HeldOutSamples, s.Status, Trials = audit ? s.Trials : [] }),
        }, new JsonSerializerOptions { WriteIndented = !audit }));
    }
}
