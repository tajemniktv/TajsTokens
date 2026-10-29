using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Research;
using TajsTokens.Infrastructure.Services;

internal static class UsualActivityCli
{
    public static async Task RunAsync(string[] args)
    {
        DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal).ToUniversalTime();
        var from = Parse(args[2]); var through = Parse(args[3]); var asOf = Parse(args[4]);
        if (from >= through || through > asOf || asOf > DateTimeOffset.UtcNow || through - from > TimeSpan.FromDays(366))
            throw new ArgumentException("Choose past from/through/as-of times, with a target range up to 366 days.");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var data = await new SqliteForecastDatasetReader(args[1]).ReadAsync("codex", "default", from, through,
            cancellation.Token, includeQuota: false);
        bool audit = args[0].EndsWith("-audit", StringComparison.Ordinal);
        var report = UsualActivityEvaluation.Evaluate(data with { CapturedAtUtc = asOf }, from, through,
            cancellation.Token, includeOrigins: audit);
        // Closed numeric projection only: no account/session/source IDs, file paths or content.
        var events = audit ? data.Tokens.Where(t => t.ObservedAtUtc <= through &&
            (t.CapturedAtUtc is null || t.CapturedAtUtc <= asOf)).Select(t => new
            { t.ObservedAtUtc, t.CapturedAtUtc, Tokens = t.ReportedTotalTokens }).ToArray() : null;
        Console.WriteLine(JsonSerializer.Serialize(new { Report = report, Events = events },
            new JsonSerializerOptions { WriteIndented = !audit }));
    }
}
