using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

internal static class WorkloadReferenceCli
{
    public static async Task RunAsync(string[] args)
    {
        var repository = new SqliteTelemetryRepository(args[1]);
        var service = new WorkloadReferenceService(repository);
        object result;
        if (args[0] is "--reference-init" or "--reference-import" or "--select-tt")
        {
            await ApiPriceCatalogCli.VerifyDestinationAsync(args[1]);
            if (args[0] == "--reference-import")
            {
                var role = args[2] switch { "official" => ApiPriceOrigin.OfficialCurated, "override" => ApiPriceOrigin.LocalOverride,
                    "fallback" => ApiPriceOrigin.CommunityFallback, _ => throw new ArgumentException("Choose official, override or fallback.") };
                var attempt = await service.ImportAsync(args[3], role, default);
                if (attempt.State != ApiPriceImportState.Accepted) Environment.ExitCode = 2;
            }
            else if (args[0] == "--select-tt") await service.SelectBasisAsync(args[2], default);
            else await service.InitializeAsync(default);
            result = await service.ReadAsync(default, initialize: false);
        }
        else
        {
            var state = await service.ReadAsync(default, initialize: false);
            var from = DateTimeOffset.Parse(args[2], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
            var to = DateTimeOffset.Parse(args[3], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
            if (from >= to || to > DateTimeOffset.UtcNow || to - from > TimeSpan.FromDays(366)) throw new ArgumentException("Choose a past range up to 366 days.");
            var data = await new SqliteForecastDatasetReader(args[1]).ReadLedgerAndQuotaAsync("codex", "default", from, to, default, includeQuota: false);
            var joint = CodexJointWorkloadBuilder.Build(data.Ledger.Select(x => x.Workload));
            result = new { FromUtc = from, ToUtc = to, data.CapturedAtUtc, Rows = data.Ledger.Count, joint.ReportedTokens,
                Scope = "Read-only selected local work under the explicitly selected TT basis and price source; no billing or quota conversion.",
                Reference = WorkloadReferenceProjection.Build(state, joint, data.Ledger, DateTimeOffset.UtcNow) };
        }
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
