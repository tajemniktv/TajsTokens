using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CalibrationEvidenceTests
{
    [Fact]
    public async Task SourceMutationsAdvanceStampButDerivedAndNoOpWritesDoNot()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        var reader = new SqliteCalibrationEvidence(f.Path);
        CalibrationEvidenceStamp initial = (await reader.ReadAsync(f.Now, default))!;
        Assert.NotNull(initial);
        await f.Repository.UpsertQuotaSnapshotAsync(f.Quota, default);
        var quota = (await reader.ReadAsync(f.Now, default))!;
        Assert.True(quota.Revision > initial.Revision);
        Assert.Equal(initial.FeatureRevision, quota.FeatureRevision);
        var forecast = QuotaPredictionService.BuildResetOutlook([f.Quota], f.Now);
        await f.Repository.UpsertForecastSnapshotAsync(new("codex", "default", forecast,
            f.Quota.Source, f.Quota.Authority, f.Quota.CapturedAtUtc, f.Quota.WindowMinutes, f.Quota.ResetsAtUtc, "account"), default);
        Assert.Equal(quota, await reader.ReadAsync(f.Now, default));
        await f.SqlAsync("UPDATE quota_snapshots SET used_percent=used_percent;");
        Assert.Equal(quota, await reader.ReadAsync(f.Now, default));
        await f.AddTokenAsync(f.Now.AddMinutes(-1));
        var tokens = (await reader.ReadAsync(f.Now, default))!;
        Assert.True(tokens.FeatureRevision > quota.FeatureRevision);
        await f.SqlAsync("UPDATE codex_native_token_events SET model='changed';");
        var changed = (await reader.ReadAsync(f.Now, default))!;
        Assert.True(changed.Revision > tokens.Revision);
        await f.SqlAsync("BEGIN; DELETE FROM codex_native_token_events; ROLLBACK;");
        Assert.Equal(changed, await reader.ReadAsync(f.Now, default));
        await f.SqlAsync("DELETE FROM codex_native_token_events;");
        Assert.True((await reader.ReadAsync(f.Now, default))!.Revision > changed.Revision);
    }

    [Fact]
    public async Task TimeBoundaryIncludesLateCollectionFutureEvidenceAndRollingWindow()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        await f.AddTokenAsync(f.Now.AddMinutes(-1), f.Now.AddMinutes(2));
        await f.AddTokenAsync(f.Now.AddMinutes(3), f.Now.AddMinutes(3));
        var reader = new SqliteCalibrationEvidence(f.Path);
        var first = (await reader.ReadAsync(f.Now, default))!;
        Assert.Equal(f.Now.AddMinutes(2), first.NextTimeBoundaryUtc);
        var second = (await reader.ReadAsync(f.Now.AddMinutes(2), default))!;
        Assert.Equal(first.Revision, second.Revision);
        Assert.Equal(f.Now.AddMinutes(3), second.NextTimeBoundaryUtc);
        var third = (await reader.ReadAsync(f.Now.AddMinutes(4), default))!;
        Assert.Equal(f.Now.AddMinutes(-1).AddDays(30).AddHours(2), third.NextTimeBoundaryUtc);
    }

    [Fact]
    public async Task MissingTriggerIsRepairedAndPhysicalReplacementChangesIdentity()
    {
        using var f = new Fixture();
        using var replacement = new Fixture();
        await f.InitializeAsync(); await replacement.InitializeAsync();
        var reader = new SqliteCalibrationEvidence(f.Path);
        var before = (await reader.ReadAsync(f.Now, default))!;
        await f.SqlAsync("DROP TRIGGER calibration_evidence_context_observations_update_v1;");
        var repaired = (await reader.ReadAsync(f.Now, default))!;
        Assert.True(repaired.Revision > before.Revision);
        var other = (await new SqliteCalibrationEvidence(replacement.Path).ReadAsync(f.Now, default))!;
        SqliteConnection.ClearAllPools();
        File.Move(replacement.Path, f.Path, true);
        var after = (await reader.ReadAsync(f.Now, default))!;
        Assert.NotEqual(repaired.StorageIdentity, after.StorageIdentity);
        Assert.Equal(other.Revision, after.Revision);
    }

    [Fact]
    public async Task SchedulerReusesCompleteGenerationThenHonorsExpiryAndChangedEvidence()
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var clock = new Clock { Now = f.Now };
        int calls = 0;
        using var service = new CalibrationProcessService("unused", f.Path, () => [], () => false, clock,
            (request, token) =>
            {
                calls++;
                return Task.FromResult<CodexCalibrationResult?>(new([], new(clock.Now, clock.Now, 1, 1,
                    [new(5d / 60, 1, "fixture", 0, 0, null, null, null, "fixture")], "fixture")));
            });
        QuotaLaneState[] lanes = [new(f.Quota.Kind, "codex", "default", f.Quota, TelemetryHealthState.Live)];
        var first = await service.TryCalibrateAsync(lanes, default);
        clock.Now = f.Now.AddMinutes(1);
        Assert.Same(first, await service.TryCalibrateAsync(lanes, default));
        Assert.Equal(f.Now, first!.Workload!.GeneratedAtUtc);
        Assert.Equal(1, calls);
        clock.Now = f.Now.AddMinutes(5);
        Assert.NotSame(first, await service.TryCalibrateAsync(lanes, default));
        Assert.Equal(2, calls);
        await f.AddTokenAsync(clock.Now);
        clock.Now = clock.Now.AddMinutes(1);
        await service.TryCalibrateAsync(lanes, default);
        Assert.Equal(3, calls);
        // Same meter value but a genuinely newer source observation is another anchor.
        clock.Now = clock.Now.AddMinutes(1);
        await service.TryCalibrateAsync([lanes[0] with { Snapshot = f.Quota with { CapturedAtUtc = clock.Now } }], default);
        Assert.Equal(4, calls);
        clock.Now = f.Now;
        await service.TryCalibrateAsync(lanes, default);
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task FailedAndMidFlightChangedGenerationsAreNotCached()
    {
        using var f = new Fixture(); await f.InitializeAsync();
        var clock = new Clock { Now = f.Now };
        int calls = 0;
        using var service = new CalibrationProcessService("unused", f.Path, () => [], () => false, clock,
            async (request, token) =>
            {
                calls++;
                if (calls == 1) throw new IOException("fixture failure");
                if (calls == 2) await f.AddTokenAsync(clock.Now);
                return new([], new(clock.Now, null, 0, 0, [], "fixture"));
            });
        QuotaLaneState[] lanes = [new(f.Quota.Kind, "codex", "default", f.Quota, TelemetryHealthState.Live)];
        await Assert.ThrowsAsync<IOException>(() => service.TryCalibrateAsync(lanes, default));
        clock.Now = clock.Now.AddMinutes(1);
        Assert.True((await service.TryCalibrateAsync(lanes, default))!.Workload!.IsStale);
        clock.Now = clock.Now.AddMinutes(1);
        Assert.NotNull(await service.TryCalibrateAsync(lanes, default));
        clock.Now = clock.Now.AddMinutes(1);
        Assert.NotNull(await service.TryCalibrateAsync(lanes, default));
        Assert.Equal(3, calls);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("tajstokens-calibration-evidence-");
        public string Path => System.IO.Path.Combine(_directory.FullName, "telemetry.db");
        public DateTimeOffset Now { get; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public SqliteTelemetryRepository Repository { get; }
        public SqliteCodexObservatoryStore Store { get; }
        public QuotaSnapshot Quota => new(QuotaWindowKind.Weekly, Now, 20, 10080, Now.AddDays(5),
            "codex", "default", "codex-app-server:codex", "account");
        public Fixture() { Repository = new(Path); Store = new(Path); }
        public async Task InitializeAsync() { await Repository.InitializeIntelligenceAsync(default); await Store.InitializeAsync(default); }
        public Task AddTokenAsync(DateTimeOffset at, DateTimeOffset? collected = null) => Store.ApplyCumulativeTokenObservationAsync(
            new(Guid.NewGuid().ToString("N"), "fixture.jsonl", "s", null, at, "m", "high", 100, 0, 0, 10, 0, 110)
            { CapturedAtUtc = collected ?? at }, default);
        public async Task SqlAsync(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={Path}");
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = sql; await command.ExecuteNonQueryAsync();
        }
        public void Dispose() { Store.Dispose(); SqliteConnection.ClearAllPools(); _directory.Delete(true); }
    }
}
