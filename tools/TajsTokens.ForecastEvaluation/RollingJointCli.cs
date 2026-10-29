using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Services;

internal static class RollingJointCli
{
    public static async Task RunAsync(string[] args)
    {
        var from = DateTimeOffset.Parse(args[2], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
        var to = DateTimeOffset.Parse(args[3], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
        if (from >= to || to > DateTimeOffset.UtcNow || to - from > TimeSpan.FromDays(366)) throw new ArgumentException("Choose a past range up to 366 days.");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        bool audit = args[0].EndsWith("-audit", StringComparison.Ordinal);
        bool strict = args[0].Contains("strict", StringComparison.Ordinal);
        var data = await new SqliteForecastDatasetReader(args[1]).ReadAsync("codex", "default", from, to, cancellation.Token);
        Console.Error.WriteLine("Evaluating nominal-target rolling joint comparison.");
        var report = RollingJointComparison.Evaluate(data with { CapturedAtUtc = to },
            strict ? ForecastReplayAvailability.CollectedByOrigin : ForecastReplayAvailability.ReconstructedEventTime, cancellation.Token,
            audit ? capture => Console.WriteLine(JsonSerializer.Serialize(new
            {
                RecordType = "fit", capture.Cohort, capture.Candidate, capture.OriginUtc, capture.HorizonHours, capture.Projected, capture.PredictedDelta,
                Model = capture.Candidate.StartsWith("prefix-categories-", StringComparison.Ordinal) ? "categories" : "joint-categories",
                Training = capture.Training.Select(r => new { r.StartUtc, r.EndUtc, r.LowerDelta, r.UpperDelta, r.Features.Tokens,
                    r.TokenCategories, Joint = r.Features.JointWorkload, r.TokenCostEvidenceAvailableAtUtc }),
            })) : null, includeTrials: audit);
        Console.WriteLine(JsonSerializer.Serialize(new { RecordType = "report", FromUtc = from, ToUtc = to, Report = report },
            new JsonSerializerOptions { WriteIndented = !audit }));
    }
}
