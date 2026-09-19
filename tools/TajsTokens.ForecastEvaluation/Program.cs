using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

if (args.Length == 2 && args[0] == "--reconciliation")
{
    var home = Path.GetFullPath(args[1]);
    if (!Directory.Exists(home)) throw new ArgumentException("Codex home must exist.");
    var files = new[] { "sessions", "archived_sessions", "archive" }.Select(x => Path.Combine(home, x))
        .Where(Directory.Exists).SelectMany(x => Directory.EnumerateFiles(x, "*.jsonl", SearchOption.AllDirectories));
    var report = await TajsTokens.Infrastructure.Ingestion.CodexReconciliationAudit.RunAsync(files);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (args.Length >= 2 && args[0] is "--server-evidence" or "--probe-server-evidence" or "--collect-server-evidence" or "--declare-current-rollouts" or "--daily-pairing" or "--collect-daily-evidence")
{
    await ServerEvidenceCli.RunAsync(args);
    return;
}

IReadOnlyList<RolloutAccountAssociation> associations = [];
if (args.Length == 4 && args[2] == "--settings" && args[0] is "--cost" or "--composed" or "--composed-strict")
{
    var settings = System.Text.Json.JsonSerializer.Deserialize<RuntimeSettings>(File.ReadAllText(args[3]))
        ?? throw new ArgumentException("Settings file is empty.");
    if (settings.SchemaVersion > RuntimeSettings.CurrentSchemaVersion) throw new ArgumentException("Unsupported settings schema.");
    associations = settings.RolloutAccountAssociations ?? [];
    args = args[..2];
}

if (args.Length == 2 && args[0] == "--transfer")
{
    var data = await new SqliteForecastDatasetReader(args[1]).ReadAsync("codex", "default",
        DateTimeOffset.UnixEpoch.AddDays(1), DateTimeOffset.UtcNow, CancellationToken.None);
    var report = QuotaTransferEvaluator.Evaluate(data);
    Console.WriteLine(report.Version + ": " + report.Methodology);
    Console.WriteLine("comparison,horizon,model,source_train,destination_train,heldout,generations,scale,unscaled_loss,scaled_loss,local_loss,status");
    var index = 0;
    foreach (var score in report.Scores)
        Console.WriteLine(FormattableString.Invariant($"{++index},{score.HorizonHours},{score.Model},{score.SourceTraining},{score.DestinationTraining},{score.HeldOut},{score.HeldOutGenerations},{score.Scale:F4},{score.UnscaledLoss:F4},{score.ScaledLoss:F4},{score.LocalOnlyLoss:F4},{score.Status}"));
    if (report.Scores.Count == 0) Console.WriteLine("No compatible recorded-account regimes available; transfer and TT are unsupported.");
    return;
}

if (args.Length == 2 && args[0] is "--composed" or "--composed-strict")
{
    var data = await new SqliteForecastDatasetReader(args[1], associations).ReadAsync("codex", "default",
        DateTimeOffset.UnixEpoch.AddDays(1), DateTimeOffset.UtcNow, CancellationToken.None);
    var report = ComposedQuotaEvaluator.Evaluate(data, availability: args[0] == "--composed-strict"
        ? ForecastReplayAvailability.CollectedByOrigin : ForecastReplayAvailability.ReconstructedEventTime);
    Console.WriteLine(report.Version + ": " + report.Methodology);
    Console.WriteLine("cohort,horizon,model,train,heldout,generations,missing_composition,forecast_loss,cost_only_loss,pace_loss,incumbent_loss,displayed_MAE,asserted_train,withheld_reasons,band_origins,reported_band_coverage,mean_band_width");
    var cohorts = report.Scores.Select(x => x.Cohort).Distinct().ToList();
    foreach (var score in report.Scores)
        Console.WriteLine(FormattableString.Invariant($"cohort-{cohorts.IndexOf(score.Cohort) + 1},{score.HorizonHours},{score.CostModel},{score.TrainingIntervals},{score.HeldOutIntervals},{score.ResetGenerations},{score.MissingComposition},{score.IntervalLoss:F4},{score.CostOnlyIntervalLoss:F4},{score.PaceIntervalLoss:F4},{score.IncumbentIntervalLoss:F4},{score.DisplayedDeltaMae:F4},{score.AssertedTrainingIntervals},{string.Join(';', score.WithheldReasons.Select(x => $"{x.Key}={x.Value}"))},{score.IntervalOrigins},{score.IntervalCoverage:F4},{score.MeanIntervalWidth:F4}"));
    Console.WriteLine("breakdowns=" + System.Text.Json.JsonSerializer.Serialize(report.Scores.Where(x => x.Breakdowns.Count > 0)
        .Select(x => new { Cohort = cohorts.IndexOf(x.Cohort) + 1, x.HorizonHours, x.CostModel, x.Availability, x.Breakdowns })));
    return;
}

if (args.Length == 2 && args[0] is "--cost" or "--cost-owned-rollouts")
{
    var data = await new SqliteForecastDatasetReader(args[1], associations).ReadAsync("codex", "default",
        DateTimeOffset.UnixEpoch.AddDays(1), DateTimeOffset.UtcNow, CancellationToken.None);
    var report = QuotaCostEvaluation.Evaluate(data, userConfirmedRolloutOwnership: args[0] == "--cost-owned-rollouts");
    Console.WriteLine(report.Version + $" (snapshot {report.DatasetCapturedAtUtc:O}): " + report.Methodology);
    Console.WriteLine(report.Coverage);
    Console.WriteLine(QuotaEvaluationCoverageBuilder.Boundary);
    Console.WriteLine(QuotaCostObservationBuilder.CoverageBoundary);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { report.EvidenceCoverage, report.ConstructionCoverage, report.CohortCoverage }));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report.Scores.Where(x => x.IncompleteCategoryTrainingIntervals > 0 || x.IncompleteCategoryHeldOutIntervals > 0)
        .Select(x => new { x.Cohort.Source, x.Cohort.Kind, x.HorizonHours, x.Candidate, x.Status, x.IncompleteCategoryTrainingIntervals, x.IncompleteCategoryHeldOutIntervals })));
    Console.WriteLine($"observations={report.Observations}; " + string.Join("; ", report.QualityCounts.Select(x => $"{x.Key}={x.Value}")));
    Console.WriteLine("cohort,source,window,horizon,model,train,train_generations,heldout,heldout_generations,interval_loss,displayed_MAE,generation_loss,residual_p10,residual_median,residual_p90,unexplained_lower,bands,intersection_rate,material_win,shift_candidates,status,rate_card,unpriced_training,unpriced_heldout,unpriced_tokens,paired_pace_loss,paired_total_loss");
    var cohorts = report.Scores.Select(x => x.Cohort).Distinct().ToList();
    foreach (var score in report.Scores)
        Console.WriteLine(FormattableString.Invariant($"cohort-{cohorts.IndexOf(score.Cohort) + 1},{score.Cohort.Source},{score.Cohort.Kind},{score.HorizonHours},{score.Candidate},{score.TrainingSamples},{score.TrainingGenerations},{score.HeldOutSamples},{score.HeldOutGenerations},{score.IntervalLoss:F4},{score.DisplayedDeltaMae:F4},{score.GenerationMeanIntervalLoss:F4},{score.ResidualP10:F4},{score.ResidualMedian:F4},{score.ResidualP90:F4},{score.UnexplainedPositiveMovement:F4},{score.BandSamples},{score.BandIntersectsTargetRate:F4},{score.MaterialWin},{score.CandidateShiftResets.Count},{score.Status},{score.RateCardVersion},{score.UnpricedTrainingIntervals},{score.UnpricedHeldOutIntervals},{score.UnpricedReportedTokens},{score.PairedPaceIntervalLoss:F4},{score.PairedTotalIntervalLoss:F4}"));
    return;
}

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
if (args.Length == 2 && args[0] == "--tokens")
{
    var data = await new SqliteForecastDatasetReader(args[1]).ReadAsync("codex", "default",
        DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture), DateTimeOffset.UtcNow,
        CancellationToken.None, includeQuota: false);
    var timer = System.Diagnostics.Stopwatch.StartNew();
    foreach (var score in TokenWorkloadPredictionService.EvaluateScores(data))
        Console.WriteLine(FormattableString.Invariant($"{score.HorizonHours},{score.Model},{score.Origins},{score.MeanAbsoluteError:F0},{score.RootMeanSquaredError:F0},{score.IntervalOrigins},{score.IntervalCoverage:F3},{score.WorkloadOrigins}"));
    Console.WriteLine($"evaluation_ms={timer.ElapsedMilliseconds}");
    timer.Restart();
    var forecast = TokenWorkloadPredictionService.Predict(data, data.CapturedAtUtc);
    Console.WriteLine($"sessions={forecast.Sessions}; observations={forecast.TokenEvents}; live_ms={timer.ElapsedMilliseconds}");
    Console.WriteLine($"activity={forecast.Activity?.State}; {forecast.Activity?.Explanation}");
    foreach (var prediction in forecast.Predictions)
        Console.WriteLine(FormattableString.Invariant($"Token +{prediction.HorizonHours}h: {prediction.ExpectedTokens:F0}; model={prediction.Model}; train={prediction.TrainingSamples}; validation={prediction.ValidationSamples}"));
    return;
}
if (args.Length == 2 && args[0] == "--tt-history")
{
    var history = await new SqliteTelemetryRepository(args[1]).GetTtEvaluationHistoryAsync("codex", "default", 20, CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(history.Select(x => new
    {
        x.Id, x.RecordedAtUtc, x.Problem, x.Snapshot?.DatasetCapturedAtUtc,
        Version = x.Snapshot?.Report.Version, Scores = x.Snapshot?.Report.Scores.Count,
        BasisIds = x.Snapshot?.Report.Scores.Where(s => s.Basis is not null).Select(s => s.Basis!.BasisId).ToArray()
    })));
    return;
}
if (args.Length == 2 && args[0] is "--tt" or "--save-tt")
{
    var now = DateTimeOffset.UtcNow;
    var data = await new SqliteForecastDatasetReader(args[1]).ReadAsync("codex", "default", now.AddDays(-30), now, CancellationToken.None);
    var report = TtEvaluator.Evaluate(data);
    if (args[0] == "--save-tt")
    {
        var snapshot = new TtEvaluationSnapshot(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, data.CapturedAtUtc,
            "codex", "default", now.AddDays(-30), now, report);
        await new SqliteTelemetryRepository(args[1]).SaveTtEvaluationAsync(snapshot, CancellationToken.None);
        Console.WriteLine("Saved original aggregate TT research snapshot: " + snapshot.Id);
    }
    Console.WriteLine(report.Version + ": " + report.Methodology);
    Console.WriteLine($"Transfer comparisons: {report.Scores.Count(x => x.IsTransfer)}; zero means no compatible chronological pair, not successful transfer.");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report.Scores.Select(x => new
    {
        x.Cohort.Kind, x.Cohort.Source, x.HorizonHours, x.Basis, x.Status, x.BasisIntervals, x.CalibrationIntervals,
        x.IsTransfer, x.BasisEndUtc, x.CalibrationEndUtc,
        BasisPlan = x.BasisCohort?.PlanType, DestinationPlan = x.Cohort.PlanType, BasisKind = x.BasisCohort?.Kind,
        x.HeldOutIntervals, x.UnsupportedIntervals, x.UnsupportedTokens, x.ResetGenerations, x.QuotaPointsPerTt,
        x.HeldOutTt, x.ScalarLoss, x.FullVectorLoss, x.RawTokenLoss, x.ScalarMae, x.FullVectorMae, x.RawTokenMae, x.ZeroLoss,
        x.ScalarBias, x.CumulativeError, x.CumulativeErrorLower, x.CumulativeErrorUpper
    })));
    return;
}
if (args.Length == 2 && args[0] == "--session-quota")
{
    var now = DateTimeOffset.UtcNow;
    var data = await new SqliteForecastDatasetReader(args[1]).ReadAsync("codex", "default",
        now.AddDays(-30), now, CancellationToken.None);
    var report = SessionQuotaEvaluator.Evaluate(data);
    Console.WriteLine(report.Version + ": " + report.Methodology);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report.Scores.Select(x => new
    {
        x.Cohort.Kind, x.Cohort.Source, x.HorizonHours, x.TrainingIntervals, x.WithheldIntervals,
        Outcomes = x.Trials.Count, x.ResetGenerations, x.ActiveOutcomes, x.ConditionalMae, x.ConditionalPaceMae,
        x.ExpectedMae, x.PaceMae, x.IntervalLoss, x.PaceIntervalLoss, x.BandOrigins, x.ReportedBandCoverage, x.MeanBandWidth,
        x.IncumbentPairedOrigins, x.PairedExpectedMae, x.IncumbentMae, x.PairedExpectedIntervalLoss, x.IncumbentIntervalLoss
    })));
    return;
}
if (args.Length == 2 && args[0] == "--session-outlook")
{
    var now = DateTimeOffset.UtcNow;
    var data = await new SqliteForecastDatasetReader(args[1]).ReadAsync("codex", "default",
        now.AddDays(-30), now, CancellationToken.None, includeQuota: false);
    var report = SessionWorkloadPredictionService.Evaluate(data, data.CapturedAtUtc);
    Console.WriteLine(report.Policy + " (live-path 30-day lookback): " + report.Methodology);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { report.Scores, report.Current }));
    return;
}
if (args.Length == 2 && args[0] is "--evaluate" or "--quota")
{
    var data = await new SqliteForecastDatasetReader(args[1]).ReadAsync("codex", "default",
        DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture), DateTimeOffset.UtcNow, CancellationToken.None);
    var report = QuotaForecastEvaluation.Evaluate(data, includeTokenEvaluation: args[0] != "--quota");
    Console.WriteLine(report.Methodology);
    Console.WriteLine(report.QuotaHistorySummary);
    Console.WriteLine($"quota={report.AuthoritativeQuotaObservations}; workload={report.WorkloadObservations}; tokens={report.TokenObservations}; effort={report.TokensWithEffort}; collected={report.TokensWithCollectionTime}");
    var accounts = report.Scores.Select(x => x.AccountKey).Distinct().ToList();
    var cohorts = report.Scores.Select(x => x.HistoryCohort).Distinct().ToList();
    Console.WriteLine("cohort,account_cohort,source,kind,model,target,availability,n,nonoverlap,epochs,MAE,RMSE,band_n,coverage,fitted");
    foreach (var score in report.Scores.Where(x => x.Origins > 0))
        Console.WriteLine(FormattableString.Invariant($"cohort-{cohorts.IndexOf(score.HistoryCohort) + 1},{(score.AccountKey is null ? "unknown" : $"account-{accounts.IndexOf(score.AccountKey) + 1}")},{score.Source},{score.Kind},{score.Model},{score.Target},{score.Availability},{score.Origins},{score.NonOverlappingOrigins},{score.ResetGenerations},{score.MeanAbsoluteError:F3},{score.RootMeanSquaredError:F3},{score.IntervalOrigins},{score.IntervalCoverage:F3},{score.FittedOrigins}"));
    if (args[0] == "--quota") return;
    Console.WriteLine(TokenWorkloadPredictionService.Methodology);
    Console.WriteLine("token_horizon,model,n,MAE_tokens,RMSE_tokens,band_n,coverage,workload_origins");
    foreach (var score in report.TokenScores)
        Console.WriteLine(FormattableString.Invariant($"{score.HorizonHours},{score.Model},{score.Origins},{score.MeanAbsoluteError:F0},{score.RootMeanSquaredError:F0},{score.IntervalOrigins},{score.IntervalCoverage:F3},{score.WorkloadOrigins}"));
    var tokenTimer = System.Diagnostics.Stopwatch.StartNew();
    var tokenForecast = TokenWorkloadPredictionService.Predict(data, data.CapturedAtUtc);
    Console.WriteLine($"Token production: sessions={tokenForecast.Sessions}; observations={tokenForecast.TokenEvents}; elapsed_ms={tokenTimer.ElapsedMilliseconds}");
    foreach (var prediction in tokenForecast.Predictions)
        Console.WriteLine(FormattableString.Invariant($"Token +{prediction.HorizonHours}h: {prediction.ExpectedTokens:F0}; model={prediction.Model}; train={prediction.TrainingSamples}; validation={prediction.ValidationSamples}"));
    var scenarioHistory = CodexScenarioHistoryBuilder.Build(data);
    var scenario = new ScenarioPlannerService().Estimate(new ScenarioRequest(0.5, 1, 0), scenarioHistory, data.CapturedAtUtc);
    Console.WriteLine($"Scenario 5h: {scenario.FiveHour.Explanation}");
    Console.WriteLine($"Scenario weekly: {scenario.Weekly.Explanation}");
    foreach (var stream in data.Quota.Where(x => x.Authority == QuotaObservationAuthority.ProviderAuthoritative)
                 .GroupBy(QuotaHistoryPolicy.Cohort))
    {
        var productionRows = stream.OrderBy(x => x.CapturedAtUtc).ToArray();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var forecast = new ForecastingService().BuildForecast(productionRows, productionRows[^1].CapturedAtUtc);
        var predictions = QuotaPredictionService.Predict(data with { Quota = productionRows }, productionRows[^1], productionRows[^1].CapturedAtUtc);
        Console.WriteLine($"Production {stream.Key.Kind} {(stream.Key.AccountKey is null ? "unknown" : $"cohort-{accounts.IndexOf(stream.Key.AccountKey) + 1}")}: {forecast.State}; model={forecast.Evidence?.Model}; calibration={forecast.Evidence?.CalibrationEpochs}; elapsed_ms={timer.ElapsedMilliseconds}");
        foreach (var prediction in predictions)
            Console.WriteLine($"  +{prediction.HorizonHours}h: {prediction.RemainingPercent:0.##}% remaining; model={prediction.Model}; train={prediction.TrainingSamples}; validation={prediction.ValidationSamples}");
    }
    return;
}
if (args.Length != 1) throw new ArgumentException("Usage: ForecastEvaluation <local telemetry.db>; read-only, aggregate output only.");
using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[0], Mode = SqliteOpenMode.ReadOnly }.ToString());
connection.Open();
using var command = connection.CreateCommand();
command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('quota_snapshots') WHERE name='account_key'";
var hasAccountKey = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
command.CommandText = $"SELECT provider,profile,kind,source,captured_at_utc,used_percent,window_minutes,resets_at_utc,{(hasAccountKey ? "account_key" : "''")} FROM quota_snapshots WHERE used_percent BETWEEN 0 AND 100 AND resets_at_utc IS NOT NULL ORDER BY provider,profile,kind,source,captured_at_utc";
var rows = new List<QuotaSnapshot>();
using (var reader = command.ExecuteReader())
    while (reader.Read())
        rows.Add(new QuotaSnapshot(Enum.Parse<QuotaWindowKind>(reader.GetString(2)), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture), reader.GetString(0), reader.GetString(1), reader.GetString(3), reader.GetString(8) is { Length: > 0 } account ? account : null));
Console.WriteLine("Source-separated replay; embedded timestamps are event-time reconstruction, not proof of historical ingestion availability. Near-reset values are proxies within 5 minutes, not invented exact reset outcomes. Exhaustion negatives require a reading at reset. Bands use earlier completed generations only, one score per epoch at similar lead. No raw identifiers exported.");
Console.WriteLine("account_cohort,kind,source,model,target,n,epochs,MAE_pp,RMSE_pp,band_n,coverage,width_pp,exhaustion_label_n,classification_accuracy,eta_n,eta_bracket_MAE_h");
var accountCohorts = rows.Select(x => x.AccountKey).Distinct().ToList();
foreach (var stream in rows.GroupBy(x => (x.Provider, x.Profile, x.Kind, x.Source, x.AccountKey)))
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
        Console.WriteLine(FormattableString.Invariant($"{(stream.Key.AccountKey is null ? "unknown" : $"cohort-{accountCohorts.IndexOf(stream.Key.AccountKey) + 1}")},{stream.Key.Kind},{stream.Key.Source},{model},{trials[0].Target},{trials.Count},{trials.Select(x => x.ResetUtc).Distinct().Count()},{errors.Average(Math.Abs):F3},{Math.Sqrt(errors.Average(x => x * x)):F3},{bands.Length},{Metric(bands.Select(x => x.ObservedRemaining >= x.LowerRemaining && x.ObservedRemaining <= x.UpperRemaining ? 1d : 0d))},{Metric(bands.Select(x => x.UpperRemaining!.Value - x.LowerRemaining!.Value))},{labels.Length},{Metric(labels.Select(x => x.PredictedExhaustion == x.ObservedExhaustion ? 1d : 0d))},{etas.Length},{Metric(etas.Select(x => x.ExhaustionEtaBracketErrorHours!.Value))}"));
    }
}
