using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaForecastComputationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tajstokens-forecast-journal-").FullName;
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-10-29T12:00:00Z");
    private string Database => Path.Combine(_directory, "telemetry.db");
    private readonly Clock _clock = new() { Now = Origin.AddSeconds(3) };
    private SqliteTelemetryRepository Repository => new(Database, _clock);

    [Fact]
    public void BuilderPreservesSelectedOutputsAndReusesDirectFallbackWithoutChangingForecast()
    {
        var history = History();
        Forecast forecast = Forecast(history);
        var batch = QuotaForecastComputationBuilder.Build(history[^1], forecast, history, QuotaForecastComputationPhase.Detailed)!;
        Assert.Equal(forecast.GeneratedAtUtc, batch.GeneratedAtUtc);
        Assert.Equal(history[^1].CollectedAtUtc, batch.AnchorCollectedUtc);
        Assert.Equal(history[^1].CapturedAtUtc, batch.AnchorMeasuredUtc);
        Assert.Equal(forecast.ProjectedRemainingAtResetPercent,
            Assert.Single(batch.Points, x => x.Role == QuotaForecastPointRole.SelectedReset).RemainingPercent);
        Assert.Equal(new[] { 5d, 15d, 30d, 120d }, batch.Points.Where(x => x.Role == QuotaForecastPointRole.DirectBaseline).Select(x => x.HorizonHours * 60));
        foreach (var point in batch.Points.Where(x => x.Role == QuotaForecastPointRole.DirectBaseline))
            Assert.Equal(QuotaPaceModels.ProjectRemaining(history, QuotaPaceModels.ProductionFallback, point.HorizonHours), point.RemainingPercent);
        Assert.Null(forecast.Evidence!.HorizonPredictions);
        Assert.Null(QuotaForecastComputationBuilder.Build(history[^1] with { AccountKey = null }, forecast, history, QuotaForecastComputationPhase.Detailed));
    }

    [Fact]
    public void FlatAndResetCrossingComparatorsKeepExplicitLimitations()
    {
        var history = History().Select(x => x with { UsedPercent = 20, ResetsAtUtc = Origin.AddMinutes(25) }).ToArray();
        var batch = QuotaForecastComputationBuilder.Build(history[^1], Forecast(history), history, QuotaForecastComputationPhase.CompactPace)!;
        var points = batch.Points.Where(x => x.Role == QuotaForecastPointRole.DirectBaseline).ToArray();
        Assert.Equal(80, points[0].RemainingPercent);
        Assert.Equal(ForecastSupportReason.FlatMeter, points[0].SupportReasons);
        Assert.Null(points[2].RemainingPercent);
        Assert.Equal(ForecastSupportReason.MissingOrExpiredReset, points[2].SupportReasons);
        var sparse = history.TakeLast(1).ToArray();
        var missing = QuotaForecastComputationBuilder.Build(sparse[0], Forecast(sparse), sparse, QuotaForecastComputationPhase.CompactPace)!;
        Assert.All(missing.Points.Where(x => x.Role == QuotaForecastPointRole.DirectBaseline), point => Assert.Null(point.RemainingPercent));
    }

    [Fact]
    public async Task RetryReopenAndConcurrentRetryPreserveActualFirstRecordingAndOutputVariants()
    {
        var repository = Repository;
        var batch = Batch();
        var original = await repository.RecordForecastComputationAsync(batch, default);
        Assert.Equal(_clock.Now, original.RecordedAtUtc);
        Assert.Equal(_clock.Now, original.ConfirmedAtUtc);
        _clock.Now = Origin.AddMinutes(10);
        var retried = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Repository.RecordForecastComputationAsync(batch, default)));
        Assert.All(retried, x => { Assert.Equal(original.Id, x.Id); Assert.Equal(original.RecordedAtUtc, x.RecordedAtUtc); Assert.Equal(original.ConfirmedAtUtc, x.ConfirmedAtUtc); });
        var changed = batch with { Points = batch.Points.Select((x, i) => i == 0 ? x with { RemainingPercent = 42 } : x).ToArray() };
        var variant = await repository.RecordForecastComputationAsync(changed, default);
        Assert.NotEqual(original.Id, variant.Id);
        Assert.Equal(_clock.Now, variant.RecordedAtUtc);
        var read = await Read();
        Assert.Equal(2, read.Count);
        Assert.Equal(batch.Points[0].RemainingPercent, read[0].Computation!.Points[0].RemainingPercent);
        Assert.All(read, x => Assert.Null(x.Problem));
    }

    [Fact]
    public async Task RecordingClockSeparatesLateOutputsAndClockAnomaliesFromProspectivePredictions()
    {
        _clock.Now = Origin.AddMinutes(5);
        var batch = Batch();
        var entry = await Repository.RecordForecastComputationAsync(batch, default);
        var direct = entry.Computation!.Points.Where(x => x.Role == QuotaForecastPointRole.DirectBaseline).ToArray();
        Assert.Equal(QuotaForecastRecordingTiming.AtOrAfterTarget, entry.Timing(direct[0]));
        Assert.Equal(QuotaForecastRecordingTiming.BeforeTarget, entry.Timing(direct[1]));
        var invalid = await Repository.RecordForecastComputationAsync(batch with { GeneratedAtUtc = _clock.Now.AddSeconds(1) }, default);
        Assert.Equal(QuotaForecastRecordingTiming.InvalidClock, invalid.Timing(invalid.Computation!.Points[2]));
        var futureCollection = await Repository.RecordForecastComputationAsync(batch with { AnchorCollectedUtc = _clock.Now.AddSeconds(2) }, default);
        Assert.Equal(QuotaForecastRecordingTiming.InvalidClock, futureCollection.Timing(futureCollection.Computation!.Points[2]));
        _clock.Now = Origin.AddDays(2);
        var replay = await Repository.RecordForecastComputationAsync(batch with { Phase = QuotaForecastComputationPhase.Detailed }, default);
        Assert.All(replay.Computation!.Points, point => Assert.Equal(QuotaForecastRecordingTiming.AtOrAfterTarget, replay.Timing(point)));
    }

    [Fact]
    public async Task PostCommitClockRatherThanInsertClockQualifiesBeforeTargetOutput()
    {
        var repository = Repository;
        await repository.InitializeIntelligenceAsync(default);
        int samples = 0;
        _clock.Read = () => ++samples == 1 ? Origin.AddSeconds(3) : Origin.AddMinutes(6);
        var entry = await repository.RecordForecastComputationAsync(Batch(), default);
        Assert.Equal(Origin.AddSeconds(3), entry.RecordedAtUtc);
        Assert.Equal(Origin.AddMinutes(6), entry.ConfirmedAtUtc);
        var point = Assert.Single(entry.Computation!.Points, x => x.Role == QuotaForecastPointRole.DirectBaseline && x.HorizonHours < .1);
        Assert.Equal(QuotaForecastRecordingTiming.AtOrAfterTarget, entry.Timing(point));
    }

    [Fact]
    public async Task InterruptedConfirmationCannotGainAnEarlierTimeWhenRetried()
    {
        var repository = Repository;
        await repository.InitializeIntelligenceAsync(default);
        using var cancel = new CancellationTokenSource();
        int samples = 0;
        _clock.Read = () => { if (++samples == 2) cancel.Cancel(); return Origin.AddSeconds(3); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.RecordForecastComputationAsync(Batch(), cancel.Token));
        var pending = Assert.Single(await Read());
        Assert.Null(pending.ConfirmedAtUtc);
        Assert.Equal(QuotaForecastRecordingTiming.Unconfirmed, pending.Timing(pending.Computation!.Points[1]));
        _clock.Read = null; _clock.Now = Origin.AddMinutes(6);
        var retried = await Repository.RecordForecastComputationAsync(Batch(), default);
        Assert.Equal(pending.RecordedAtUtc, retried.RecordedAtUtc);
        Assert.Equal(_clock.Now, retried.ConfirmedAtUtc);
        Assert.Equal(QuotaForecastRecordingTiming.AtOrAfterTarget, retried.Timing(retried.Computation!.Points[1]));
    }

    [Fact]
    public async Task MigrationNeverRelabelsMutableHistoryAndReadDoesNotMigrate()
    {
        await Repository.InitializeIntelligenceAsync(default);
        var batch = Batch();
        await Repository.UpsertForecastSnapshotAsync(new("codex", "default", Forecast(History())), default);
        await Sql("DROP TABLE quota_forecast_computations; UPDATE intelligence_schema SET version=11;");
        Assert.Empty(await Read());
        Assert.Equal(11L, await Scalar("SELECT version FROM intelligence_schema"));
        var reopened = Repository;
        await reopened.InitializeIntelligenceAsync(default);
        Assert.Equal(12L, await Scalar("SELECT version FROM intelligence_schema"));
        Assert.Empty(await Read());
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM forecast_snapshots"));
        await reopened.RecordForecastComputationAsync(batch, default);
        Assert.Single(await Read());
        await Sql("UPDATE intelligence_schema SET version=13;");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repository.InitializeIntelligenceAsync(default));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM quota_forecast_computations"));
    }

    [Fact]
    public async Task PaginationScopeAndCorruptionRemainExplicit()
    {
        var batch = Batch();
        var original = await Repository.RecordForecastComputationAsync(batch, default);
        for (int i = 0; i < 3; i++)
            await Repository.RecordForecastComputationAsync(batch with { GeneratedAtUtc = Origin.AddSeconds(i) }, default);
        var first = Assert.Single(await Repository.ReadForecastComputationsAsync("codex", "default", Origin, Origin.AddDays(1), 1, default));
        var rest = await Repository.ReadForecastComputationsAsync("codex", "default", Origin, Origin.AddDays(1), 10, default, first.RecordedAtUtc, first.Id);
        Assert.Equal(2, rest.Count);
        Assert.DoesNotContain(rest, x => x.Id == first.Id);
        Assert.Empty(await Repository.ReadForecastComputationsAsync("codex", "other", Origin, Origin.AddDays(1), 10, default));
        await Sql("UPDATE quota_forecast_computations SET payload='{}' WHERE computation_id='" + original.Id + "'");
        var bad = Assert.Single(await Read(), x => x.Problem is not null);
        Assert.Null(bad.Computation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repository.RecordForecastComputationAsync(batch, default));
    }

    [Fact]
    public async Task InvalidOrCancelledWritesCannotReplaceAnOriginal()
    {
        var batch = Batch();
        var original = await Repository.RecordForecastComputationAsync(batch, default);
        await Assert.ThrowsAsync<ArgumentException>(() => Repository.RecordForecastComputationAsync(batch with { Contract = "raw-unknown" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Repository.RecordForecastComputationAsync(batch with { Points = [batch.Points[0] with { RemainingPercent = double.NaN }] }, default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Repository.RecordForecastComputationAsync(batch, cancel.Token));
        Assert.Equal(original.Id, Assert.Single(await Read()).Id);
    }

    [Fact]
    public async Task CompactForecastOwnerRecordsOutputWithoutReadingTrainingData()
    {
        var repository = Repository;
        await repository.InitializeIntelligenceAsync(default);
        var history = History();
        foreach (var row in history) await repository.UpsertQuotaSnapshotAsync(row, default);
        var service = new SqliteIntelligenceService(Database, repository);
        var result = Assert.Single(await service.BuildBackgroundForecastsAsync(
            [new(history[^1].Kind, "codex", "default", history[^1], TelemetryHealthState.Live, Origin)], Origin, default));
        var entry = Assert.Single(await Read());
        Assert.Equal(QuotaForecastComputationPhase.CompactPace, entry.Computation!.Phase);
        Assert.Equal(result.Forecast!.ProjectedRemainingAtResetPercent, entry.Computation.Points[0].RemainingPercent);
        Assert.Equal(QuotaForecastRecordingTiming.BeforeTarget, entry.Timing(entry.Computation.Points[1]));
        Assert.Null(result.Forecast.Evidence!.HorizonPredictions);
        var detailed = Assert.Single(await service.BuildAndPersistCurrentForecastsAsync(
            [new(history[^1].Kind, "codex", "default", history[^1], TelemetryHealthState.Live, Origin)], Origin, default));
        var detailedEntry = Assert.Single(await Read(), x => x.Computation!.Phase == QuotaForecastComputationPhase.Detailed);
        Assert.Equal(detailed.Forecast!.Evidence!.HorizonPredictions!.Count,
            detailedEntry.Computation!.Points.Count(x => x.Role == QuotaForecastPointRole.SelectedHorizon));
    }

    private static QuotaSnapshot[] History() => Enumerable.Range(0, 5).Select(i => new QuotaSnapshot(
        QuotaWindowKind.FiveHour, Origin.AddMinutes(-60 + 15 * i), 10 + i * 5, 300,
        Origin.AddHours(3), "codex", "default", "codex-app-server:test", "account")
        { HasSourceTimestamp = true, CollectedAtUtc = Origin.AddMinutes(-60 + 15 * i), ObservationId = "obs-" + i }).ToArray();
    private static Forecast Forecast(QuotaSnapshot[] history) => QuotaPredictionService.BuildResetOutlook(history, Origin);
    private static QuotaForecastComputation Batch() => QuotaForecastComputationBuilder.Build(History()[^1], Forecast(History()), History(), QuotaForecastComputationPhase.CompactPace)!;
    private Task<IReadOnlyList<QuotaForecastComputationEntry>> Read() => Repository.ReadForecastComputationsAsync("codex", "default", Origin, Origin.AddDays(4), 100, default);
    private async Task Sql(string sql) { await using var c = new SqliteConnection($"Data Source={Database}"); await c.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
    private async Task<object?> Scalar(string sql) { await using var c = new SqliteConnection($"Data Source={Database}"); await c.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return await cmd.ExecuteScalarAsync(); }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } public Func<DateTimeOffset>? Read { get; set; } public override DateTimeOffset GetUtcNow() => Read?.Invoke() ?? Now; }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
}
