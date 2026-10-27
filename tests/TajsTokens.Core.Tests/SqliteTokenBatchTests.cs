using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;

namespace TajsTokens.Core.Tests;

public sealed class SqliteTokenBatchTests
{
    [Theory]
    [InlineData(false, 390)]
    [InlineData(true, 410)]
    public async Task OrderedBatchMatchesSingleWritesAcrossSessionsDuplicatesResetAndReplay(bool reportedLastUsage, long expectedTotal)
    {
        DirectoryInfo folder = Directory.CreateTempSubdirectory("tajstokens-token-batch-");
        try
        {
            var at = new DateTimeOffset(2026,10,1,10,0,0,TimeSpan.Zero);
            CodexTokenCountObservation Observation(string id, string session, int input, int output, int seconds) =>
                new(id,"rollout.jsonl",session,null,at.AddSeconds(seconds),"model","high",
                    input,0,0,output,0,input+output) { CapturedAtUtc=at.AddDays(1) };
            var one=Observation("one","a",100,10,1);
            var two=Observation("two","b",200,20,2);
            var three=Observation("three","a",150,20,3);
            var reset=new CodexTokenCountObservation("reset","rollout.jsonl","a",null,at.AddSeconds(4),"model","high",
                10,0,0,10,0,20,reportedLastUsage?new CodexTokenUsageSnapshot(10,0,0,10,0,20):null)
                { CapturedAtUtc=at.AddDays(1) };
            CodexTokenCountObservation[] observations=[one,two,three,three,reset];
            var results=new List<CodexTokenAccountingSnapshot>();
            foreach(bool batch in new[]{false,true})
            {
                string database=Path.Combine(folder.FullName,batch?"batch.db":"single.db");
                using(var store=new SqliteCodexObservatoryStore(database))
                {
                    await store.InitializeAsync(default);
                    if(batch) await store.ApplyCumulativeTokenObservationsAsync(observations,default);
                    else foreach(var item in observations) await store.ApplyCumulativeTokenObservationAsync(item,default);
                }
                using(var restarted=new SqliteCodexObservatoryStore(database))
                    await restarted.ApplyCumulativeTokenObservationsAsync(observations,default);
                results.Add(await new SqliteNativeCodexAccountingProvider(database).GetSnapshotAsync(default));
            }
            Assert.Equal(expectedTotal, results[0].Usage.Sum(x=>x.Breakdown.Total));
            Assert.Equal(JsonSerializer.Serialize(results[0]),JsonSerializer.Serialize(results[1]));
        }
        finally { SqliteConnection.ClearAllPools(); folder.Delete(true); }
    }

    [Fact]
    public async Task FailedTokenBatchDoesNotAdvanceCheckpointAndRestartsWithoutDoubleCounting()
    {
        DirectoryInfo folder = Directory.CreateTempSubdirectory("tajstokens-token-batch-recovery-");
        try
        {
            string database=Path.Combine(folder.FullName,"data.db");
            string file=Path.Combine(folder.FullName,"rollout-2026-10-01T10-00-00-11111111-1111-4111-8111-111111111111.jsonl");
            await File.WriteAllLinesAsync(file,[
                """{"timestamp":"2026-10-01T10:00:00Z","type":"session_meta","payload":{"id":"11111111-1111-4111-8111-111111111111"}}""",
                """{"timestamp":"2026-10-01T10:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":20,"cache_write_input_tokens":0,"output_tokens":10,"reasoning_output_tokens":2,"total_tokens":110}}}}""",
                """{"timestamp":"2026-10-01T10:00:02Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":200,"cached_input_tokens":40,"cache_write_input_tokens":0,"output_tokens":20,"reasoning_output_tokens":4,"total_tokens":220}}}}"""
            ]);
            var repository=new SqliteTelemetryRepository(database);
            using var store=new SqliteCodexObservatoryStore(database);
            await store.InitializeAsync(default);
            await using(var connection=new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync(); using var command=connection.CreateCommand();
                command.CommandText="""
                    CREATE TRIGGER fail_second_token BEFORE INSERT ON codex_native_token_events
                    WHEN (SELECT COUNT(*) FROM codex_native_token_events)>0
                    BEGIN SELECT RAISE(ABORT,'injected token failure'); END;
                    """;
                await command.ExecuteNonQueryAsync();
            }
            var writer=new SqliteCodexIngestionBatchWriter(database,store);
            var ingestion=new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(),repository,store,writer);
            await Assert.ThrowsAsync<SqliteException>(()=>ingestion.IngestAsync(file,default));
            Assert.Null(await repository.GetCheckpointAsync(file,default));
            Assert.Empty((await new SqliteNativeCodexAccountingProvider(database).GetSnapshotAsync(default)).Usage);
            await using(var connection=new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync(); using var command=connection.CreateCommand();
                command.CommandText="DROP TRIGGER fail_second_token;"; await command.ExecuteNonQueryAsync();
            }
            using var restarted=new SqliteCodexObservatoryStore(database);
            var recovery=new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(),repository,restarted,
                new SqliteCodexIngestionBatchWriter(database,restarted));
            await recovery.IngestAsync(file,default);
            Assert.Equal(new FileInfo(file).Length,(await repository.GetCheckpointAsync(file,default))?.LastByteOffset);
            Assert.Equal(220,(await new SqliteNativeCodexAccountingProvider(database).GetSnapshotAsync(default)).Usage.Sum(x=>x.Breakdown.Total));
        }
        finally { SqliteConnection.ClearAllPools(); folder.Delete(true); }
    }
}
