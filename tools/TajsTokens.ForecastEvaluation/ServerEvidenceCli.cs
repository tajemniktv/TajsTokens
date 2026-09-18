using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;
using TajsTokens.Infrastructure.Services;
using System.Text.Json;

internal static class ServerEvidenceCli
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] is not ("--server-evidence" or "--probe-server-evidence" or "--collect-server-evidence" or "--declare-current-rollouts"))
            throw new ArgumentException("Unknown server-evidence mode or invalid argument count; no data was read or written.");
        if (args[0] == "--declare-current-rollouts" && args.Length != 3)
            throw new ArgumentException("Explicit declaration requires the existing settings path.");
        if (args.Length < 2 || !File.Exists(args[1])) throw new ArgumentException("Provide an existing TajsTokens database.");
        if (args.Length == 3)
        {
            if (!File.Exists(args[2])) throw new ArgumentException("Settings path must already exist.");
            var saved = JsonSerializer.Deserialize<RuntimeSettings>(await File.ReadAllTextAsync(args[2]));
            if (saved is null || saved.SchemaVersion > RuntimeSettings.CurrentSchemaVersion)
                throw new ArgumentException("Cannot use invalid or newer settings.");
        }
        var settingsStore = args.Length == 3 ? new RuntimeSettingsStore(args[2]) : null;
        var settings = settingsStore?.Load();
        var repository = new SqliteTelemetryRepository(args[1]);
        var service = new CodexServerEvidenceService(args[1], repository, new CodexAppServerEvidenceProvider(),
            () => settings?.RolloutAccountAssociations ?? []);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        CodexServerCollection? transient = null;
        switch (args[0])
        {
            case "--probe-server-evidence":
            case "--declare-current-rollouts":
                transient = await new CodexAppServerEvidenceProvider().CollectAsync(await service.SelectThreadsAsync(timeout.Token), timeout.Token);
                break;
            case "--collect-server-evidence":
                await service.CollectAsync(true, timeout.Token);
                break;
            case "--server-evidence":
                break;
            default: throw new ArgumentException("Unknown server-evidence mode.");
        }
        var report = await service.CompareAsync(timeout.Token, transient?.Observations);
        if (args[0] == "--declare-current-rollouts")
        {
            if (settingsStore is null || settings is null) throw new ArgumentException("Explicit declaration requires the existing settings path.");
            var current = report.LatestObservations.Where(x => x.Surface == CodexServerSurface.AccountActivity)
                .OrderByDescending(x => x.CollectedAtUtc).FirstOrDefault();
            if (current is not { State: ServerEvidenceState.Available, AccountEvidence: AccountEvidenceClass.ServerCorrelated, CorrelatedAccountKey: { } account } ||
                DateTimeOffset.UtcNow - current.CollectedAtUtc > TimeSpan.FromMinutes(2))
                throw new InvalidOperationException("No fresh, consistently bracketed current account; declaration not saved.");
            var now = DateTimeOffset.UtcNow;
            var data = await new SqliteForecastDatasetReader(args[1]).ReadAsync("codex", "default", DateTimeOffset.UnixEpoch.AddDays(1), now, timeout.Token);
            var created = RolloutAccountAssociationPolicy.Create(data, account, now);
            // Preserve prior assertions, including contradictory ones: the existing resolver fails
            // closed rather than silently overwriting ownership. Do not duplicate identical coverage.
            var added = created.Where(c => !settings.RolloutAccountAssociations.Any(a => a.AccountKey == c.AccountKey &&
                a.SourceIdentity == c.SourceIdentity && a.SessionId == c.SessionId && a.FromUtc <= c.FromUtc && a.ThroughUtc >= c.ThroughUtc)).ToArray();
            settingsStore.Save(settings with { RolloutAccountAssociations = settings.RolloutAccountAssociations.Concat(added).ToArray() });
            settings = settingsStore.Load();
            Console.WriteLine($"User-declared single-account association saved for {added.Length} bounded retained source/session ranges. Native AccountKeys unchanged; assertions are revocable in Settings.");
            report = await service.CompareAsync(timeout.Token, transient?.Observations);
        }
        Console.WriteLine(transient is null ? service.Status : "Read-only live probe: existing TajsTokens database was not migrated or written.");
        Console.WriteLine(report.ToDisplayText());
    }
}
