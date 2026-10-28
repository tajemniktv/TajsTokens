using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Services;

internal static class JointScoringCli
{
    // Explicit local candidate scoring only. It never selects/activates a ruler or saves a score to daily data.
    public static async Task RunAsync(string database, string definition, string fromText, string toText)
    {
        DateTimeOffset from = DateTimeOffset.Parse(fromText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
        DateTimeOffset to = DateTimeOffset.Parse(toText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
        if (from >= to || to - from > TimeSpan.FromDays(366) || to > DateTimeOffset.UtcNow)
            throw new ArgumentException("Choose an ordered past UTC range of at most 366 days.");
        using var stream = File.OpenRead(definition);
        if (stream.Length > 512 * 1024) throw new ArgumentException("Basis definition exceeds 512 KiB.");
        using var json = await JsonDocument.ParseAsync(stream);
        var basis = json.RootElement.Deserialize<TtJointWorkloadBasis>() ?? throw new ArgumentException("Missing basis definition.");
        if (json.RootElement.GetProperty("BasisId").GetString() != basis.BasisId ||
            json.RootElement.GetProperty("ReferenceScore").GetDecimal() != basis.ReferenceScore)
            throw new ArgumentException("Basis identity or normalization differs from the saved definition.");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var data = await new SqliteForecastDatasetReader(database).ReadLedgerAndQuotaAsync("codex", "default", from, to, cancellation.Token, includeQuota: false);
        var workload = CodexJointWorkloadBuilder.Build(data.Ledger.Select(x => x.Workload));
        var assessment = TtJointScoring.Evaluate(basis, workload);
        Console.WriteLine(JsonSerializer.Serialize(new { Scorer = TtJointScoring.Version, basis.BasisId, basis.Convention, basis.Vintage,
            FromUtc = from, ToUtc = to, DatasetCapturedAtUtc = data.CapturedAtUtc, SelectedLocalRows = data.Ledger.Count,
            Scope = "Selected installation-local native increments; no account-wide completeness or current-login ownership is inferred. Explicit candidate ruler only; no activation or write.",
            Assessment = assessment, assessment.ExactTt, assessment.Explanation }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
