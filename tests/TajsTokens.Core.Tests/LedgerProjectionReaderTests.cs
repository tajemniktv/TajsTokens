using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class LedgerProjectionReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectionMatchesFullLedgerAndQuotaIncludingBoundsAndAttribution(bool conflicting)
    {
        using var f = new CalibrationEvidenceTests.Fixture();
        await f.InitializeAsync();
        await SeedAsync(f);
        DateTimeOffset from = f.Now.AddHours(-1);
        RolloutAccountAssociation[] claims = conflicting
            ? [Claim("one", "account"), Claim("two", "other")]
            : [Claim("one", "account")];
        var reader = new SqliteForecastDatasetReader(f.Path, claims);
        var full = await reader.ReadAsync("codex", "default", from, f.Now, default, includeLedger: true);
        var projected = await reader.ReadLedgerAndQuotaAsync("codex", "default", from, f.Now, default, null);

        Assert.Equal(JsonSerializer.Serialize(full.Ledger), JsonSerializer.Serialize(projected.Ledger));
        Assert.Equal(JsonSerializer.Serialize(full.Quota), JsonSerializer.Serialize(projected.Quota));
        Assert.Equal(new[] { "from", "inside", "unowned" }, projected.Ledger.Select(row => row.ObservationId));
        Assert.All(projected.Ledger, row => Assert.Equal("native-thread", row.ThreadId));
        var inside = Assert.Single(projected.Ledger, row => row.ObservationId == "inside");
        Assert.Null(inside.Workload.Model);
        Assert.Equal(f.Now.AddMinutes(15), inside.CollectedAtUtc);
        Assert.Equal(conflicting ? AccountEvidenceClass.Conflicting : AccountEvidenceClass.UserDeclaredSingleAccount, inside.Ownership);
        Assert.Null(Assert.Single(projected.Ledger, row => row.ObservationId == "unowned").SourceIdentity);
        Assert.Empty(projected.Workload);
        Assert.Empty(projected.Tokens);
        Assert.Empty(projected.Context);
        Assert.Same(claims, projected.AccountAssociations);

        var scoped = await reader.ReadLedgerAndQuotaAsync("codex", "default", from, f.Now, default, "account", includeQuota: false);
        Assert.Empty(scoped.Quota);
        Assert.Equal(JsonSerializer.Serialize(projected.Ledger), JsonSerializer.Serialize(scoped.Ledger));

        RolloutAccountAssociation Claim(string id, string account) => new(id, "codex", "default", "valid", "s", from, f.Now, account, f.Now);
    }

    [Fact]
    public async Task ProjectionDoesNotReadUnusedPredictiveTables()
    {
        using var f = new CalibrationEvidenceTests.Fixture();
        await f.InitializeAsync();
        await SeedAsync(f);
        var reader = new SqliteForecastDatasetReader(f.Path);
        var full = await reader.ReadAsync("codex", "default", f.Now.AddHours(-1), f.Now, default, includeLedger: true);
        await f.SqlAsync("DROP TABLE codex_workload_observations; DROP TABLE context_observations;");
        var projected = await reader.ReadLedgerAndQuotaAsync("codex", "default", f.Now.AddHours(-1), f.Now, default, null);
        Assert.Equal(JsonSerializer.Serialize(full.Ledger), JsonSerializer.Serialize(projected.Ledger));
        Assert.Equal(JsonSerializer.Serialize(full.Quota), JsonSerializer.Serialize(projected.Quota));
    }

    [Fact]
    public async Task ProjectionStillRefusesTruncationAtTheTokenReadBound()
    {
        using var f = new CalibrationEvidenceTests.Fixture();
        await f.InitializeAsync();
        await f.SqlAsync("""
            WITH RECURSIVE n(value) AS (SELECT 1 UNION ALL SELECT value+1 FROM n WHERE value<500001)
            INSERT INTO codex_native_token_events(source_event_id,source_file,session_id,observed_at_utc,
                captured_at_utc,source_event_at_utc,source_timestamp_quality,counter_epoch,
                uncached_input_tokens,cache_read_tokens,cache_write_tokens,non_reasoning_output_tokens,
                reasoning_output_tokens,reported_total_tokens)
            SELECT 'bounded-'||value,'fixture.jsonl','s','2026-10-01T11:30:00.0000000+00:00',
                '2026-10-01T11:30:00.0000000+00:00','2026-10-01T11:30:00.0000000+00:00','Valid',0,
                1,0,0,0,0,1 FROM n;
            """);
        await Assert.ThrowsAsync<ForecastDatasetReadLimitException>(() =>
            new SqliteForecastDatasetReader(f.Path).ReadLedgerAndQuotaAsync("codex", "default",
                f.Now.AddHours(-1), f.Now, default, null));
    }

    private static async Task SeedAsync(CalibrationEvidenceTests.Fixture f)
    {
        await f.Store.UpsertRolloutFileAsync("valid", "fixture.jsonl", "s", 20, f.Now, default);
        await f.Repository.UpsertQuotaSnapshotAsync(f.Quota, default);
        await f.SqlAsync("""
            INSERT INTO sessions(session_id,thread_id,repository,started_at_utc,status)
            VALUES('s','native-thread','project','2026-10-01T09:00:00.0000000+00:00','complete');
            INSERT INTO codex_workload_observations(source_record_id,source_identity,source_file,start_byte_offset,end_byte_offset,
                session_id,event_type,observed_at_utc,captured_at_utc,model,reasoning_effort,contract_version)
            VALUES('meta','valid',(SELECT file_path FROM rollout_files WHERE source_identity='valid'),0,1,'s','turn_context',
                '2026-10-01T09:00:00.0000000+00:00','2026-10-01T09:00:00.0000000+00:00','m','high','codex-workload/v1');
            """);
        await using var connection = new SqliteConnection($"Data Source={f.Path};Pooling=False");
        await connection.OpenAsync();
        using var fileCommand = connection.CreateCommand();
        fileCommand.CommandText = "SELECT file_path FROM rollout_files WHERE source_identity='valid';";
        string fileLabel = (string)(await fileCommand.ExecuteScalarAsync())!;
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO codex_native_token_events(source_event_id,source_file,session_id,observed_at_utc,
                captured_at_utc,source_event_at_utc,source_timestamp_quality,model,reasoning_effort,counter_epoch,
                uncached_input_tokens,cache_read_tokens,cache_write_tokens,non_reasoning_output_tokens,
                reasoning_output_tokens,reported_total_tokens)
            VALUES($id,$file,'s',$at,$captured,$at,'Valid',$model,NULL,0,10,20,0,3,2,35);
            """;
        command.Parameters.Add("$id", SqliteType.Text);
        command.Parameters.Add("$file", SqliteType.Text);
        command.Parameters.Add("$at", SqliteType.Text);
        command.Parameters.Add("$captured", SqliteType.Text);
        command.Parameters.Add("$model", SqliteType.Text);
        foreach (var row in new[]
        {
            (Id: "lookback", At: f.Now.AddHours(-2)), (Id: "from", At: f.Now.AddHours(-1)),
            (Id: "inside", At: f.Now.AddMinutes(-2)), (Id: "unowned", At: f.Now.AddMinutes(-1)), (Id: "to", At: f.Now),
        })
        {
            command.Parameters[0].Value = row.Id;
            command.Parameters[1].Value = row.Id == "unowned" ? "missing.jsonl" : fileLabel;
            command.Parameters[2].Value = row.At.ToString("O");
            command.Parameters[3].Value = (row.Id == "inside" ? f.Now.AddMinutes(15) : row.At).ToString("O");
            command.Parameters[4].Value = row.Id == "inside" ? DBNull.Value : "m";
            await command.ExecuteNonQueryAsync();
        }
    }
}
