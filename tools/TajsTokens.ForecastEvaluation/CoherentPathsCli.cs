using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Infrastructure.Services;

internal static class CoherentPathsCli
{
    public static async Task RunAsync(string[] args)
    {
        DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
        var from = Parse(args[2]); var to = Parse(args[3]);
        if (from >= to || to > DateTimeOffset.UtcNow || to - from > TimeSpan.FromDays(366))
            throw new ArgumentException("Choose an ordered past UTC target range up to 366 days.");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var data = await new SqliteForecastDatasetReader(args[1]).ReadAsync("codex", "default", from, to, cancellation.Token);
        var report = CoherentQuotaPathStudy.Evaluate(data with { CapturedAtUtc = to },
            args[0].Contains("strict", StringComparison.Ordinal) ? ForecastReplayAvailability.CollectedByOrigin : ForecastReplayAvailability.ReconstructedEventTime,
            cancellation.Token, includeSteps: args[0].EndsWith("-audit", StringComparison.Ordinal));
        Console.WriteLine(JsonSerializer.Serialize(new { FromUtc = from, ToUtc = to, Report = report },
            new JsonSerializerOptions { WriteIndented = !args[0].EndsWith("-audit", StringComparison.Ordinal) }));
    }
}
