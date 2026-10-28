using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

internal static class ApiPriceCatalogCli
{
    public static async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { Environment.ExitCode = 130; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or SqliteException or IOException or FormatException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Price command failed ({e.GetType().Name}); no raw source error is emitted.");
            Environment.ExitCode = 2;
        }
    }
    public static async Task ImportAsync(string database, string sourceKey, string file)
    {
        // Never initialize an arbitrary existing/native SQLite file as app-owned telemetry.
        if (!File.Exists(database)) throw new ArgumentException("An initialized TajsTokens telemetry database is required.");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('quota_snapshots','forecast_snapshots','usage_events');";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 3)
                throw new ArgumentException("Destination is not an initialized TajsTokens telemetry database.");
        }
        var attempt = await ApiPriceCatalogFileReader.ReadAsync(sourceKey, file, default);
        await new SqliteTelemetryRepository(database).SavePriceCatalogImportAsync(attempt, default);
        Console.WriteLine(JsonSerializer.Serialize(new { attempt.SourceKey, State = attempt.State.ToString(), attempt.AttemptedAtUtc,
            SnapshotId = attempt.Snapshot?.SnapshotId, Origin = attempt.Snapshot?.Origin.ToString(), attempt.Issue,
            ActiveSourceChanged = false, TtBasisChanged = false }));
        if (attempt.State != ApiPriceImportState.Accepted) Environment.ExitCode = 2;
    }
    public static async Task PreviewAsync(string database, string file, string fromText, string toText, string ageText)
    {
        var from = DateTimeOffset.Parse(fromText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
        var to = DateTimeOffset.Parse(toText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
        double age = double.Parse(ageText, CultureInfo.InvariantCulture);
        if (from >= to || to - from > TimeSpan.FromDays(366) || to > DateTimeOffset.UtcNow || !double.IsFinite(age) || age <= 0 || age > 3650)
            throw new ArgumentException("Choose an ordered past range of at most 366 days and a positive source-age limit up to 3650 days.");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var attempt = await ApiPriceCatalogFileReader.ReadAsync("explicit-preview", file, cancellation.Token);
        if (attempt.Snapshot is not { ReferenceContext: { } context } snapshot)
            throw new ArgumentException("A valid snapshot with an explicit reference context is required.");
        var data = await new SqliteForecastDatasetReader(database).ReadLedgerAndQuotaAsync("codex", "default", from, to, cancellation.Token, includeQuota: false);
        var joint = CodexJointWorkloadBuilder.Build(data.Ledger.Select(x => x.Workload));
        var items = joint.Cells.Concat(joint.Overflow is { } overflow ? [overflow] : []).Select(ApiPriceCatalogValuation.FromJoint).ToArray();
        var valuation = ApiPriceCatalogValuation.Calculate(snapshot, items, context, ApiPriceTimeMode.SnapshotReference, DateTimeOffset.UtcNow, TimeSpan.FromDays(age));
        Console.WriteLine(JsonSerializer.Serialize(new { snapshot.SnapshotId, Origin = snapshot.Origin.ToString(), snapshot.RetrievedAtUtc, snapshot.SourceArtifactSha256,
            FromUtc = from, ToUtc = to, MaximumSourceAgeDays = age, SelectedLocalRows = data.Ledger.Count,
            Scope = "Explicit snapshot-reference revaluation of selected local native counts; actual billing conditions and historical effective dates are not inferred. No activation or write.",
            Valuation = valuation }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
