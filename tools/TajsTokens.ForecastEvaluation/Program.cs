using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

if ((args.Length == 4 && args[0] == "--prepare") || (args.Length == 3 && args[0] == "--backfill"))
{
    var destination = Path.GetFullPath(args[^1]);
    if (args[0] == "--prepare")
    {
        if (File.Exists(destination)) throw new ArgumentException("Preparation destination must be a new local database; existing evidence is never overwritten.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[2], Mode = SqliteOpenMode.ReadOnly }.ToString());
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination }.ToString());
        source.Open(); target.Open(); source.BackupDatabase(target);
    }
    else if (!File.Exists(destination)) throw new ArgumentException("Backfill requires an existing TajsTokens database.");
    Console.WriteLine("Replaying content-free native metadata into the explicitly selected TajsTokens database; Codex sources remain read-only.");
    var repository = new SqliteTelemetryRepository(destination);
    await repository.InitializeAsync(CancellationToken.None);
    var runtime = CodexObservatoryRuntimeFactory.Create(destination, repository);
    var service = new CodexObservatoryService(runtime.Ingestion, runtime.Store,
        new[] { "sessions", "archived_sessions", "archive" }.Select(x => Path.Combine(args[1], x)));
    var result = await service.RefreshAsync(CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    if (result.Errors > 0) Environment.ExitCode = 1;
    return;
}
if (args.Length == 3 && args[0] == "--inventory")
{
    NativeInventory.Run(args[1], args[2]);
    return;
}
if (args.Length == 2 && args[0] == "--evaluate")
{
    var data = await new SqliteForecastDatasetReader(args[1]).ReadAsync("codex", "default",
        DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture), DateTimeOffset.UtcNow, CancellationToken.None);
    var report = QuotaForecastEvaluation.Evaluate(data);
    Console.WriteLine(report.Methodology);
    Console.WriteLine($"quota={report.AuthoritativeQuotaObservations}; workload={report.WorkloadObservations}; tokens={report.TokenObservations}; effort={report.TokensWithEffort}; collected={report.TokensWithCollectionTime}");
    Console.WriteLine("kind,model,target,availability,n,epochs,MAE,RMSE,band_n,coverage,fitted");
    foreach (var score in report.Scores)
        Console.WriteLine(FormattableString.Invariant($"{score.Kind},{score.Model},{score.Target},{score.Availability},{score.Origins},{score.ResetGenerations},{score.MeanAbsoluteError:F3},{score.RootMeanSquaredError:F3},{score.IntervalOrigins},{score.IntervalCoverage:F3},{score.FittedOrigins}"));
    var scenarioHistory = CodexScenarioHistoryBuilder.Build(data);
    var scenario = new ScenarioPlannerService().Estimate(new ScenarioRequest(0.5, 1, 0), scenarioHistory, data.CapturedAtUtc);
    Console.WriteLine($"Scenario 5h: {scenario.FiveHour.Explanation}");
    Console.WriteLine($"Scenario weekly: {scenario.Weekly.Explanation}");
    foreach (var stream in data.Quota.GroupBy(x => (x.Kind, x.Source)))
    {
        var productionRows = stream.OrderBy(x => x.CapturedAtUtc).ToArray();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var forecast = new ForecastingService().BuildForecast(productionRows, productionRows[^1].CapturedAtUtc);
        Console.WriteLine($"Production {stream.Key.Kind}: {forecast.State}; model={forecast.Evidence?.Model}; calibration={forecast.Evidence?.CalibrationEpochs}; elapsed_ms={timer.ElapsedMilliseconds}");
    }
    return;
}
if (args.Length != 1) throw new ArgumentException("Usage: ForecastEvaluation <local telemetry.db>; read-only, aggregate output only.");
using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[0], Mode = SqliteOpenMode.ReadOnly }.ToString());
connection.Open();
using var command = connection.CreateCommand();
command.CommandText = "SELECT provider,profile,kind,source,captured_at_utc,used_percent,window_minutes,resets_at_utc FROM quota_snapshots WHERE used_percent BETWEEN 0 AND 100 AND resets_at_utc IS NOT NULL ORDER BY provider,profile,kind,source,captured_at_utc";
var rows = new List<QuotaSnapshot>();
using (var reader = command.ExecuteReader())
    while (reader.Read())
        rows.Add(new QuotaSnapshot(Enum.Parse<QuotaWindowKind>(reader.GetString(2)), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture), reader.GetString(0), reader.GetString(1), reader.GetString(3)));
Console.WriteLine("Source-separated replay; embedded timestamps are event-time reconstruction, not proof of historical ingestion availability. Near-reset values are proxies within 5 minutes, not invented exact reset outcomes. Exhaustion negatives require a reading at reset. Bands use earlier completed generations only, one score per epoch at similar lead. No raw identifiers exported.");
Console.WriteLine("kind,source,model,target,n,epochs,MAE_pp,RMSE_pp,band_n,coverage,width_pp,exhaustion_label_n,classification_accuracy,eta_n,eta_bracket_MAE_h");
foreach (var stream in rows.GroupBy(x => (x.Provider, x.Profile, x.Kind, x.Source)))
{
    foreach (var model in QuotaPaceModels.Candidates.Append("adaptive"))
    foreach (var horizon in new[] { 0.5, 2.0, 24.0, -1.0 })
    {
        var trials = model == "adaptive"
            ? QuotaForecastBacktester.ReplayAdaptive(stream.ToArray(), horizon < 0 ? null : horizon)
            : QuotaForecastBacktester.Replay(stream.ToArray(), model, horizon < 0 ? null : horizon);
        if (trials.Count == 0) continue;
        var errors = trials.Select(x => x.PredictedRemaining - x.ObservedRemaining).ToArray();
        var bands = trials.Where(x => x.LowerRemaining is not null).ToArray();
        var labels = trials.Where(x => x.ObservedExhaustion is not null).ToArray();
        var etas = trials.Where(x => x.ExhaustionEtaBracketErrorHours is not null).ToArray();
        string Metric(IEnumerable<double> values) => values.Any() ? values.Average().ToString("F3", CultureInfo.InvariantCulture) : "NA";
        Console.WriteLine(FormattableString.Invariant($"{stream.Key.Kind},{stream.Key.Source},{model},{trials[0].Target},{trials.Count},{trials.Select(x => x.ResetUtc).Distinct().Count()},{errors.Average(Math.Abs):F3},{Math.Sqrt(errors.Average(x => x * x)):F3},{bands.Length},{Metric(bands.Select(x => x.ObservedRemaining >= x.LowerRemaining && x.ObservedRemaining <= x.UpperRemaining ? 1d : 0d))},{Metric(bands.Select(x => x.UpperRemaining!.Value - x.LowerRemaining!.Value))},{labels.Length},{Metric(labels.Select(x => x.PredictedExhaustion == x.ObservedExhaustion ? 1d : 0d))},{etas.Length},{Metric(etas.Select(x => x.ExhaustionEtaBracketErrorHours!.Value))}"));
    }
}
