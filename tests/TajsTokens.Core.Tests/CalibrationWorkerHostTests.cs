using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CalibrationWorkerHostTests
{
    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task InvalidRequestsAreRejectedWithoutSourceAccess(string request)
    {
        using var output = new StringWriter();
        Assert.Equal(2, await CalibrationWorkerHost.RunAsync(new StringReader(request), output));
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task HeadlessWorkerUsesOnlyProvidedAppDatabaseAndReturnsTypedResult()
    {
        string folder = Directory.CreateTempSubdirectory("tajstokens-worker-").FullName;
        try
        {
            string database = Path.Combine(folder, "telemetry.db");
            var repository = new SqliteTelemetryRepository(database);
            using var store = new SqliteCodexObservatoryStore(database);
            await repository.InitializeIntelligenceAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);
            string cache = Path.Combine(folder, "cache.db");
            var request = new CalibrationWorkerRequest(database, cache, [], []);
            using var output = new StringWriter();
            int status = await CalibrationWorkerHost.RunAsync(new StringReader(JsonSerializer.Serialize(request)), output);
            Assert.Equal(0, status);
            CodexCalibrationResult result = JsonSerializer.Deserialize<CodexCalibrationResult>(output.ToString())!;
            Assert.Empty(result.Forecasts);
            Assert.NotNull(result.Workload);
            Assert.Equal(0, result.Workload.TokenEvents);
            Assert.True(File.Exists(cache));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }
}
