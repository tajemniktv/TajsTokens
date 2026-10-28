using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Infrastructure.Services;

internal static class ContinuationCli
{
    public static async Task RunAsync(string[] args)
    {
        var from = DateTimeOffset.Parse(args[2], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
        var to = DateTimeOffset.Parse(args[3], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
        if (from >= to || to > DateTimeOffset.UtcNow || to - from > TimeSpan.FromDays(366)) throw new ArgumentException("Choose a past range up to 366 days.");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var data = await new SqliteForecastDatasetReader(args[1]).ReadAsync("codex", "default", from, to, cancellation.Token);
        var report = WorkloadContinuationEvaluation.Evaluate(data, args[0] == "--continuation-contexts-strict"
            ? ForecastReplayAvailability.CollectedByOrigin : ForecastReplayAvailability.ReconstructedEventTime, cancellation.Token);
        var cohorts = report.Scores.Select(x => x.Cohort).Distinct().ToList();
        Console.WriteLine(JsonSerializer.Serialize(new { FromUtc = from, ToUtc = to, data.CapturedAtUtc, report.Version, report.Methodology,
            ContextHours = WorkloadContinuationEvaluation.ContextHours,
            Scores = report.Scores.Select(x => new { Cohort = cohorts.IndexOf(x.Cohort), Window = x.Cohort.Kind.ToString(), x.HorizonHours, x.ContextHours,
                Availability = x.Availability.ToString(), x.TargetIntervals, x.AvailableContexts, x.MatchedTargets, x.WithheldTargets,
                x.QuietOrOldPositiveContexts, x.PositiveCompositionTargets, x.TokenMae, x.TokenWape, x.SignedAccumulatedTokenError, x.MeanJointShareL1, x.EvidenceSupport }) }));
    }
}
