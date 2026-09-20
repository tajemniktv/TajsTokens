// Taj's Tokens | CodexServerEvidenceTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;
using TajsTokens.Infrastructure.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class CodexServerEvidenceTests : IDisposable
{
    private const string Thread = "019a0000-0000-7000-8000-000000000001";
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);
    private readonly string _directory;

    public CodexServerEvidenceTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx"))) root = root.Parent;
        _directory = Path.Combine(root!.FullName, ".codex", "temp", "server-evidence-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    private string Database => Path.Combine(_directory, "telemetry.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    [Theory]
    [InlineData(3, 4)]
    [InlineData(4, 5)]
    public async Task ParserUpgradeInvalidatesOnlySelectionCacheAtomicallyAndCanRetry(int prior, int next)
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeAsync(default);
        await repository.SaveServerEvidenceAsync(
            new CodexServerCollection([Parse("""{"result":{"summary":{"lifetimeTokens":123}}}""")]),
            default);
        await new SqliteCodexStateIndexStore(Database).InitializeAsync(default);
        await Execute(
            $$"""
              UPDATE codex_state_index_schema SET version={{prior}};
              INSERT INTO codex_state_thread_fingerprints VALUES('thread',42,1,NULL,NULL,0,'hash','2026-09-18');
              UPDATE codex_state_sync SET watermark_updated_at_ms=42;
              CREATE TRIGGER fail_index_upgrade BEFORE UPDATE OF version ON codex_state_index_schema
              WHEN NEW.version={{next}} BEGIN SELECT RAISE(ABORT,'test migration failure'); END;
              """);
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteCodexStateIndexStore(Database).InitializeAsync(default));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_state_thread_fingerprints"));
        Assert.Equal(42L, await Scalar("SELECT watermark_updated_at_ms FROM codex_state_sync"));
        await Execute("DROP TRIGGER fail_index_upgrade;");
        var index = new SqliteCodexStateIndexStore(Database);
        await index.InitializeAsync(default);
        Assert.Empty(await index.GetFingerprintsAsync(default));
        Assert.Equal(0, await index.GetWatermarkAsync(default));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_server_evidence"));
    }

    [Fact]
    public async Task QuotaMetadataRetainsRevisionsWithoutWindowsAndShowsLatestMissingState()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeAsync(default);
        CodexServerObservation observation = CodexAppServerQuotaProvider.ParseQuotaResponse(
            """{"result":{"accountId":"account","rateLimits":{"spendControlReached":true}}}""",
            Now).MetadataObservation!;
        await repository.SaveServerEvidenceAsync(new CodexServerCollection([observation]), default);
        await repository.SaveServerEvidenceAsync(new CodexServerCollection([observation]), default);
        CodexServerObservation changed = CodexAppServerQuotaProvider
            .ParseQuotaResponse("""{"result":{"accountId":"account","rateLimits":{}}}""", Now.AddMinutes(1)).MetadataObservation!;
        await repository.SaveServerEvidenceAsync(new CodexServerCollection([changed]), default);
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM codex_server_evidence WHERE surface='QuotaMetadata'"));
        var service = new CodexServerEvidenceService(Database, repository, new CodexBackendDailyEvidenceProvider(() => false));
        string text = await service.ReadNativeMetadataSummaryAsync(default);
        Assert.Contains("spend-control reached unknown", text);
        Assert.DoesNotContain("spend-control reached True", text);
        Assert.Contains("Missingness", text);
        Assert.Contains("legacy/spend-control-reached", text);
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM codex_server_evidence WHERE surface='QuotaMetadata'"));
    }

    [Fact]
    public async Task DailySnapshotsAreImmutableAndLatestFailureDoesNotResurrectEarlierSuccess()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeAsync(default);
        CodexDailyReport report = CodexDailyReportParser.Parse(
            """{"balance_unit":"credit","data":[{"date":"2026-09-17","totals":{"credits":0,"text_total_tokens":500}}]}""",
            false,
            "wham/analytics/daily-workspace-usage-counts",
            "2026-09-01",
            "2026-09-18");
        var row = new CodexServerObservation(
            "daily-test",
            CodexServerSurface.DailyCounts,
            null,
            Now,
            Now,
            CodexServerEvidenceParser.Contract,
            CodexDailyReportParser.Contract,
            ServerEvidenceState.Available,
            "test")
        {
            DailyReport = report, CorrelatedAccountKey = "test-account", AccountEvidence = AccountEvidenceClass.ServerCorrelated,
        };
        await repository.SaveServerEvidenceAsync(new CodexServerCollection([row]), default);
        await repository.SaveServerEvidenceAsync(new CodexServerCollection([row]), default);
        var service = new CodexServerEvidenceService(Database, repository, new CodexBackendDailyEvidenceProvider(() => false));
        CodexServerObservation saved = Assert.Single(await service.ReadDailyReportsAsync(default));
        Assert.Equal(500, Assert.Single(saved.DailyReport!.Days).TotalTokens);
        Assert.Equal(0m, saved.DailyReport.Days[0].Credits);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveServerEvidenceAsync(new CodexServerCollection([row with { Detail = "overwrite" }]), default));
        await repository.SaveServerEvidenceAsync(
            new CodexServerCollection(
            [
                row with
                {
                    Id = "daily-failed",
                    CollectedAtUtc = Now.AddMinutes(30),
                    State = ServerEvidenceState.AuthenticationRequired,
                    DailyReport = null,
                },
            ]),
            default);
        CodexServerObservation latest = Assert.Single(await service.ReadDailyReportsAsync(default));
        Assert.Equal(ServerEvidenceState.AuthenticationRequired, latest.State);
        Assert.Null(latest.DailyReport);
    }

    [Fact]
    public void AccountAllowlistKeepsUnknownSeparateFromZeroAndDoesNotRetainContent()
    {
        CodexServerObservation row = Parse(
            """{"result":{"summary":{"lifetimeTokens":0,"peakDailyTokens":null,"currentStreakDays":0,"email":"secret@example.test"},"dailyUsageBuckets":[{"startDate":"2026-09-17","tokens":0}],"prompt":"never retain"}}""");
        Assert.Equal(ServerEvidenceState.Available, row.State);
        Assert.Equal(0, row.Activity!.LifetimeTokens);
        Assert.Null(row.Activity.PeakDailyTokens);
        Assert.Equal(0, Assert.Single(row.Activity.DailyUsageBuckets!).Tokens);
        Assert.Null(row.CorrelatedAccountKey);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(row));
        Assert.DoesNotContain("never retain", JsonSerializer.Serialize(row));
        CodexServerObservation missing = Parse("""{"result":{"summary":{},"dailyUsageBuckets":null}}""");
        Assert.Equal(ServerEvidenceState.Empty, missing.State);
        Assert.Null(missing.Activity!.LifetimeTokens);
        Assert.Null(missing.Activity.DailyUsageBuckets);
    }

    [Theory]
    [InlineData("{\"result\":{\"summary\":{\"lifetimeTokens\":-1}}}")]
    [InlineData("{\"result\":{\"summary\":{\"lifetimeTokens\":1,\"lifetimeTokens\":2}}}")]
    [InlineData("{\"result\":{\"summary\":{},\"dailyUsageBuckets\":[{\"startDate\":\"2026-02-31\",\"tokens\":1}]}}")]
    [InlineData(
        "{\"result\":{\"summary\":{},\"dailyUsageBuckets\":[{\"startDate\":\"2026-09-17\",\"tokens\":1},{\"startDate\":\"2026-09-17\",\"tokens\":1}]}}")]
    public void MalformedOrAmbiguousCountersAreNotPartialSuccess(string json)
    {
        Assert.Equal(ServerEvidenceState.Invalid, Parse(json).State);
    }

    [Fact]
    public void OversizedDailyReportIsRejected()
    {
        string json = JsonSerializer.Serialize(
            new
            {
                result = new
                {
                    summary = new { lifetimeTokens = 1 },
                    dailyUsageBuckets = Enumerable.Repeat(new { startDate = "2026-09-17", tokens = 1 }, 4001),
                },
            });
        Assert.Equal(ServerEvidenceState.Invalid, Parse(json).State);
    }

    [Fact]
    public void ThreadNullNeverFallsBackToAccountSummaryAndCreditsNeverBecomeQuota()
    {
        CodexServerObservation missing = Parse("""{"result":{"summary":{"lifetimeTokens":900},"threadUsage":null}}""", Thread);
        Assert.Equal(ServerEvidenceState.Unavailable, missing.State);
        Assert.Null(missing.Activity);
        Assert.Null(missing.ThreadUsage);
        CodexServerObservation row = ThreadRow();
        Assert.Equal(ServerEvidenceState.Available, row.State);
        Assert.Equal(2500000, row.ThreadUsage!.EstimatedUsageCreditsMicros);
        Assert.Null(row.ThreadUsage.EstimatedUsageUsdMicros);
        Assert.Null(row.ThreadUsage.Groups[0].OutputTokens);
        Assert.Null(row.Activity);
        Assert.Equal(ServerEvidenceState.Conflict, Parse(ThreadJson(), "019a0000-0000-7000-8000-000000000002").State);
    }

    [Theory]
    [InlineData(-32601, "no such method", ServerEvidenceState.Unsupported)]
    [InlineData(-32600, "chatgpt authentication required to read token usage", ServerEvidenceState.AuthenticationRequired)]
    [InlineData(-32603, "secret header at https://example.test", ServerEvidenceState.Error)]
    public void CapabilityErrorsAreTypedAndSanitized(int code, string message, ServerEvidenceState expected)
    {
        CodexServerObservation row = Parse(JsonSerializer.Serialize(new { error = new { code, message } }));
        Assert.Equal(expected, row.State);
        Assert.DoesNotContain(message, row.Detail);
    }

    [Fact]
    public void CorrelationRequiresStableBracketingAndNeverClaimsProviderVerifiedIdentity()
    {
        CodexServerObservation stable = CodexAppServerEvidenceProvider.Correlate(ThreadRow(), "account-A", "account-A");
        Assert.Equal(AccountEvidenceClass.ServerCorrelated, stable.AccountEvidence);
        Assert.Equal("account-A", stable.CorrelatedAccountKey);
        CodexServerObservation changed = CodexAppServerEvidenceProvider.Correlate(ThreadRow(), "account-A", "account-B");
        Assert.Equal(ServerEvidenceState.Available, changed.State);
        Assert.Equal(AccountEvidenceClass.Conflicting, changed.AccountEvidence);
        Assert.Null(changed.CorrelatedAccountKey);
        Assert.NotNull(changed.ThreadUsage); // preserve mismatch as evidence
        CodexServerObservation unknown = CodexAppServerEvidenceProvider.Correlate(ThreadRow(), "account-A", null);
        Assert.Equal(AccountEvidenceClass.Unattributed, unknown.AccountEvidence);
        Assert.Null(unknown.CorrelatedAccountKey);
    }

    [Fact]
    public async Task AdditiveMigrationRetryAndIdentityCollisionPreserveExistingEvidence()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeAsync(default);
        await Execute(
            "DROP TABLE codex_server_evidence; PRAGMA user_version=12; INSERT INTO quota_snapshots(provider,profile,kind,captured_at_utc,source,account_key) VALUES('codex','default','Weekly','2026-09-01','codex-rollout:test','');");
        await repository.InitializeAsync(default);
        CodexServerObservation row = ThreadRow();
        await repository.SaveServerEvidenceAsync(new CodexServerCollection([row]), default);
        await repository.SaveServerEvidenceAsync(new CodexServerCollection([row]), default);
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_server_evidence"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM quota_snapshots WHERE account_key=''"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveServerEvidenceAsync(
            new CodexServerCollection([row with { Id = "new" }, row with { Detail = "rewrite" }]),
            default));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_server_evidence"));
        CodexServerComparisonReport report = await Service(repository).CompareAsync(default);
        Assert.Equal(row.Detail, Assert.Single(report.LatestObservations).Detail);
    }

    [Theory]
    [InlineData(false, ServerEvidenceState.Unavailable)]
    [InlineData(true, ServerEvidenceState.Unavailable)]
    [InlineData(true, ServerEvidenceState.Error)]
    public async Task ComparisonRetainsMismatchAndLatestNullDoesNotReuseSuccess(bool loseCorrelation, ServerEvidenceState state)
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeAsync(default);
        using var store = new SqliteCodexObservatoryStore(Database);
        await store.InitializeAsync(default);
        await Execute(
            $"""
             INSERT INTO codex_native_token_events(source_event_id,source_file,session_id,observed_at_utc,counter_epoch,
                 uncached_input_tokens,cache_read_tokens,cache_write_tokens,non_reasoning_output_tokens,reasoning_output_tokens,reported_total_tokens)
             VALUES('fixture','fixture','{Thread}','2026-09-17T10:00:00.0000000+00:00',0,80,0,0,0,0,80);
             """);
        CodexServerObservation row = CodexAppServerEvidenceProvider.Correlate(ThreadRow(), "A", "A");
        await repository.SaveServerEvidenceAsync(new CodexServerCollection([row]), default);
        CodexServerEvidenceService service = Service(repository);
        CodexServerComparisonRow comparison = (await service.CompareAsync(default)).Comparisons.First();
        Assert.Equal(100, comparison.ServerTokens);
        Assert.Equal(80, comparison.LocalTokens);
        Assert.Equal(.8, comparison.LocalToServerRatio);
        CodexServerObservation nullRow = CodexAppServerEvidenceProvider.Correlate(
                Parse("""{"result":{"threadUsage":null}}""", Thread),
                "A",
                loseCorrelation ? null : "A")
            with
            {
                CollectedAtUtc = Now.AddMinutes(1), State = state,
            };
        await repository.SaveServerEvidenceAsync(new CodexServerCollection([nullRow]), default);
        CodexServerComparisonReport later = await service.CompareAsync(default);
        Assert.Equal(state, Assert.Single(later.LatestObservations).State);
        Assert.Empty(later.Comparisons);
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM codex_server_evidence"));
        Assert.Equal(80L, await Scalar("SELECT SUM(reported_total_tokens) FROM codex_native_token_events"));
    }

    [Theory]
    [InlineData(ServerEvidenceState.Unavailable)]
    [InlineData(ServerEvidenceState.Unsupported)]
    [InlineData(ServerEvidenceState.Error)]
    public void AccountConflictDoesNotReplaceProviderOutcome(ServerEvidenceState state)
    {
        CodexServerObservation original = ThreadRow() with { State = state, Detail = "Provider outcome.", CorrelatedAccountKey = "old" };
        CodexServerObservation result = CodexAppServerEvidenceProvider.Correlate(original, "A", "B");
        Assert.Equal(state, result.State);
        Assert.Equal(AccountEvidenceClass.Conflicting, result.AccountEvidence);
        Assert.Null(result.CorrelatedAccountKey);
        Assert.StartsWith(original.Detail, result.Detail);
    }

    [Fact]
    public async Task NormalizationPreservesFetchesRevisionsOmissionsEmptyNullAndAtomicRetries()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeAsync(default);
        CodexServerObservation first = Parse(
            """{"result":{"summary":{"lifetimeTokens":300},"dailyUsageBuckets":[{"startDate":"2026-09-16","tokens":100},{"startDate":"2026-09-17","tokens":200}]}}""");
        CodexServerObservation revised = first with
        {
            Id = "revised",
            CollectedAtUtc = Now.AddMinutes(1),
            Activity = first.Activity! with
            {
                DailyUsageBuckets = [new CodexAccountDay("2026-09-16", 100), new CodexAccountDay("2026-09-17", 201)],
            },
        };
        CodexServerObservation omitted = revised with
        {
            Id = "omitted",
            CollectedAtUtc = Now.AddMinutes(2),
            Activity = revised.Activity! with { DailyUsageBuckets = [new CodexAccountDay("2026-09-17", 201)] },
        };
        CodexServerObservation empty = first with
        {
            Id = "empty", CollectedAtUtc = Now.AddMinutes(3), Activity = first.Activity! with { DailyUsageBuckets = [] },
        };
        CodexServerObservation missing = empty with
        {
            Id = "missing", CollectedAtUtc = Now.AddMinutes(4), Activity = empty.Activity! with { DailyUsageBuckets = null },
        };
        foreach (CodexServerObservation row in new[] { first, first with { Id = "repeat" }, revised, omitted, empty, missing })
        {
            await repository.SaveServerEvidenceAsync(new CodexServerCollection([row]), default);
            CodexServerObservation latest = Assert.Single((await Service(repository).CompareAsync(default)).LatestObservations);
            Assert.Equal(JsonSerializer.Serialize(row.Activity), JsonSerializer.Serialize(latest.Activity));
        }
        Assert.Equal(6L, await Scalar("SELECT COUNT(*) FROM codex_server_evidence"));
        Assert.Equal(3L, await Scalar("SELECT COUNT(*) FROM codex_account_day_values"));
        Assert.Equal(4L, await Scalar("SELECT COUNT(*) FROM codex_account_bucket_sets"));
        Assert.Equal(5L, await Scalar("SELECT COUNT(*) FROM codex_account_bucket_members"));
        await repository.SaveServerEvidenceAsync(new CodexServerCollection([first]), default);
        CodexServerObservation collision = first with
        {
            Activity = first.Activity! with { DailyUsageBuckets = [new CodexAccountDay("2026-09-17", 999)] },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveServerEvidenceAsync(new CodexServerCollection([collision]), default));
        Assert.Equal(3L, await Scalar("SELECT COUNT(*) FROM codex_account_day_values"));
        Assert.Equal(4L, await Scalar("SELECT COUNT(*) FROM codex_account_bucket_sets"));
    }

    [Fact]
    public async Task LegacyMigrationIsLosslessAndReadOnlyComparisonWorksBeforeMigration()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeAsync(default);
        await Execute("ALTER TABLE codex_server_evidence DROP COLUMN activity_bucket_set_id; PRAGMA user_version=13;");
        CodexServerObservation row = Parse(
            """{"result":{"summary":{"lifetimeTokens":1},"dailyUsageBuckets":[{"startDate":"2026-09-17","tokens":1}]}}""");
        using (var connection = new SqliteConnection("Data Source=" + Database))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO codex_server_evidence VALUES($id,'AccountActivity',NULL,NULL,'2026-09-18','2026-09-18',$contract,'Available',$json);";
            command.Parameters.AddWithValue("$id", row.Id);
            command.Parameters.AddWithValue("$contract", row.ContractVersion);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(row));
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(
            JsonSerializer.Serialize(row),
            JsonSerializer.Serialize(Assert.Single((await Service(repository).CompareAsync(default)).LatestObservations)));
        await repository.InitializeAsync(default);
        Assert.Equal(14L, await Scalar("PRAGMA user_version"));
        Assert.Equal(
            JsonSerializer.Serialize(row),
            JsonSerializer.Serialize(Assert.Single((await Service(repository).CompareAsync(default)).LatestObservations)));
        await repository.SaveServerEvidenceAsync(new CodexServerCollection([row]), default);
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_server_evidence"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_account_day_values"));
    }

    [Fact]
    public async Task FailedNormalizationMigrationRollsBackSchemaAndCanRetry()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeAsync(default);
        await Execute(
            """
            ALTER TABLE codex_server_evidence DROP COLUMN activity_bucket_set_id;
            PRAGMA user_version=13;
            INSERT INTO codex_server_evidence VALUES('bad','AccountActivity',NULL,NULL,'2026-09-18','2026-09-18','fixture','Error','invalid JSON');
            """);
        await Assert.ThrowsAsync<JsonException>(() => repository.InitializeAsync(default));
        Assert.Equal(13L, await Scalar("PRAGMA user_version"));
        Assert.Equal(
            0L,
            await Scalar("SELECT COUNT(*) FROM pragma_table_info('codex_server_evidence') WHERE name='activity_bucket_set_id'"));
        Assert.Equal("invalid JSON", await Scalar("SELECT evidence_json FROM codex_server_evidence WHERE observation_id='bad'"));
        await Execute("DELETE FROM codex_server_evidence WHERE observation_id='bad';");
        await repository.InitializeAsync(default);
        Assert.Equal(14L, await Scalar("PRAGMA user_version"));
    }

    [Fact]
    public async Task BufferedTransportRetainsMultipleLinesBoundsAndRejectsServerRequests()
    {
        string large = new('x', CodexEvidenceTransport.MaxLineCharacters);
        using var writer = new StringWriter();
        var transport = new CodexEvidenceTransport(new StringReader(large + "\nsecond\r\n"), writer);
        Assert.Equal(large, await transport.ReadLineAsync(default));
        Assert.Equal("second", await transport.ReadLineAsync(default));
        await Assert.ThrowsAsync<IOException>(() => transport.ReadLineAsync(default));
        var excessive = new CodexEvidenceTransport(new StringReader(large + "x\n"), writer);
        await Assert.ThrowsAsync<IOException>(() => excessive.ReadLineAsync(default));
        using JsonDocument request =
            JsonDocument.Parse("""{"id":"server-1","method":"future/capability","params":{"secret":"do not echo"}}""");
        Assert.True(await transport.RejectServerRequestAsync(request.RootElement, default));
        using JsonDocument response = JsonDocument.Parse(writer.ToString());
        Assert.Equal("server-1", response.RootElement.GetProperty("id").GetString());
        Assert.Equal(-32601, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.DoesNotContain("secret", writer.ToString());
        using JsonDocument ordinary = JsonDocument.Parse("""{"id":1,"result":{}}""");
        Assert.False(await transport.RejectServerRequestAsync(ordinary.RootElement, default));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ReadLineAsync(canceled.Token));
    }

    private static CodexServerObservation Parse(string json, string? thread = null)
    {
        return CodexServerEvidenceParser.ParseUsage(json, thread, Now.AddSeconds(-1), Now, "0.154.0");
    }

    private static CodexServerObservation ThreadRow()
    {
        return Parse(ThreadJson(), Thread);
    }

    private static string ThreadJson()
    {
        return
            """{"result":{"summary":{},"threadUsage":{"threadId":"THREAD","estimatedUsageCreditsMicros":2500000,"estimatedUsageUsdMicros":null,"groups":[{"model":"gpt-test","reasoningEffort":"high","speed":"standard","estimatedUsageCreditsMicros":2500000,"totalTokens":100}]}}}"""
                .Replace("THREAD", Thread);
    }

    private CodexServerEvidenceService Service(SqliteTelemetryRepository repository)
    {
        return new CodexServerEvidenceService(Database, repository, new CodexAppServerEvidenceProvider());
    }

    private async Task Execute(string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + Database);
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> Scalar(string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + Database);
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}