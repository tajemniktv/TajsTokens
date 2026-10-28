using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class ApiPriceCatalogPersistenceTests : IDisposable
{
    private readonly string _directory;
    public ApiPriceCatalogPersistenceTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx"))) root = root.Parent;
        _directory = Path.Combine(root!.FullName, ".codex", "temp", "price-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }
    private string Database => Path.Combine(_directory, "telemetry.db");
    private string FilePath => Path.Combine(_directory, "prices.json");
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
    private async Task Execute(string sql, string? payload = null)
    {
        using var connection = new SqliteConnection("Data Source=" + Database); await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        if (payload is not null) { command.Parameters.AddWithValue("$payload", payload); command.Parameters.AddWithValue("$hash", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))); }
        await command.ExecuteNonQueryAsync();
    }
    private async Task<long> Scalar(string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + Database); await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql; return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task ImportPreservesOriginalSourceTimeAndSnapshotsAndBadUpdatesRetainLastGoodAcrossRestart()
    {
        var repository = new SqliteTelemetryRepository(Database); var original = ApiPriceCatalogTests.Snapshot();
        await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(original));
        byte[] bytes = await File.ReadAllBytesAsync(FilePath); DateTime modified = File.GetLastWriteTimeUtc(FilePath);
        var attempt = await ApiPriceCatalogFileReader.ReadAsync("official", FilePath, default);
        Assert.Equal(ApiPriceImportState.Accepted, attempt.State); Assert.Equal(original.SnapshotId, attempt.Snapshot!.SnapshotId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(FilePath)); Assert.Equal(modified, File.GetLastWriteTimeUtc(FilePath));
        await repository.SavePriceCatalogImportAsync(attempt, default);
        await repository.SavePriceCatalogImportAsync(attempt with { AttemptedAtUtc = attempt.AttemptedAtUtc.AddMinutes(1) }, default);
        var source = await new SqliteTelemetryRepository(Database).ReadPriceCatalogSourceAsync("official", default);
        Assert.Equal(attempt.AttemptedAtUtc, source.FirstImportedAtUtc); Assert.Equal(original.RetrievedAtUtc, source.LastGood!.RetrievedAtUtc);
        var replacement = ApiPriceCatalogTests.Snapshot(ApiPriceOrigin.OfficialCurated, ApiPriceCatalogTests.Rate(4));
        await repository.SavePriceCatalogImportAsync(new("official", attempt.AttemptedAtUtc.AddMinutes(2), ApiPriceImportState.Accepted, replacement, null), default);
        await repository.SavePriceCatalogImportAsync(new("official", attempt.AttemptedAtUtc.AddMinutes(3), ApiPriceImportState.Invalid, null, "invalid-price-schema"), default);
        var reopened = new SqliteTelemetryRepository(Database);
        var retained = await reopened.ReadPriceCatalogSourceAsync("official", default);
        Assert.Equal(ApiPriceImportState.Invalid, retained.LastState); Assert.Equal(replacement.SnapshotId, retained.LastGood!.SnapshotId);
        Assert.Equal(original.SnapshotId, (await reopened.ReadPriceCatalogSourceAsync("official", default, original.SnapshotId)).LastGood!.SnapshotId);
        Assert.Equal(2, await Scalar("SELECT COUNT(*) FROM api_price_catalog_snapshots"));
        Assert.Equal(2, (await reopened.ReadPriceCatalogHistoryAsync("official", 20, default)).Count);
        Assert.Equal(0, await Scalar("SELECT COUNT(*) FROM tt_joint_score_snapshots"));
    }

    [Fact]
    public async Task CandidateAdapterKeepsOnlyAllowlistedNumericDataAndDoesNotExecuteOrRetainUnknownPayloads()
    {
        await File.WriteAllTextAsync(FilePath, """
            {"Contract":"api-price-reference-candidate/v1","Currency":"USD","RateUnit":"per-1000000-tokens",
            "Product":"text-reference","Tier":"standard","ContextBand":"short","CacheWritePolicy":"30m",
            "LocallyCollectedAtUtc":"2026-10-01T00:00:00Z","EffectiveFromUtc":null,"EffectiveToUtc":null,
            "Sources":["https://developers.openai.com/api/docs/pricing"],"execute":"SECRET-INSTRUCTION",
            "Rates":[{"Model":"unknown-future-model","UncachedInput":2,"CacheRead":null,"CacheWrite":2.5,"NonReasoningOutput":10,"ReasoningOutput":10,
            "Source":"https://developers.openai.com/api/docs/pricing","credential":"SECRET-CREDENTIAL"}]}
            """);
        var result = await ApiPriceCatalogFileReader.ReadAsync("preview", FilePath, default);
        Assert.Equal(ApiPriceImportState.Accepted, result.State); Assert.Equal(5, result.Snapshot!.Rates.Count);
        Assert.All(result.Snapshot.Rates, x => Assert.Equal("unknown-future-model", x.Coordinate.Model));
        Assert.Null(result.Snapshot.Rates.Single(x => x.Category == TtTokenCategory.CacheRead).UsdPerMillion);
        Assert.All(result.Snapshot.Rates, x => Assert.Null(x.EffectiveFromUtc));
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(result));
        Assert.NotNull(result.Snapshot.SourceArtifactSha256);
        await File.WriteAllTextAsync(FilePath, "{\"Contract\":\"future\",\"execute\":\"secret\"}");
        var failed = await ApiPriceCatalogFileReader.ReadAsync("preview", FilePath, default);
        Assert.Equal(ApiPriceImportState.Invalid, failed.State); Assert.Null(failed.Snapshot);
    }

    [Theory]
    [InlineData("2026-10-01")]
    [InlineData("2026-10-01T12:00:00")]
    public async Task UnqualifiedDatesDoNotBecomeEffectiveUtcInstants(string date)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(ApiPriceCatalogTests.Snapshot()))!;
        node["Rates"]![0]!["EffectiveFromUtc"] = date;
        await File.WriteAllTextAsync(FilePath, node.ToJsonString());
        Assert.Equal(ApiPriceImportState.Invalid, (await ApiPriceCatalogFileReader.ReadAsync("source", FilePath, default)).State);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("snapshot-id")]
    [InlineData("currency")]
    [InlineData("oversized")]
    public async Task CorruptStoredSnapshotsRemainUnavailableAndCannotBeSilentlyOverwritten(string defect)
    {
        var repository = new SqliteTelemetryRepository(Database); var original = ApiPriceCatalogTests.Snapshot();
        await repository.SavePriceCatalogImportAsync(new("official", ApiPriceCatalogTests.At, ApiPriceImportState.Accepted, original, null), default);
        if (defect == "checksum") await Execute("UPDATE api_price_catalog_snapshots SET content_hash='bad'");
        else if (defect == "oversized") await Execute("UPDATE api_price_catalog_snapshots SET payload=printf('%.*c',524289,'x')");
        else
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(original))!;
            if (defect == "snapshot-id") node["SnapshotId"] = "bad-id";
            else node["Currency"] = "EUR";
            await Execute("UPDATE api_price_catalog_snapshots SET payload=$payload,content_hash=$hash", node.ToJsonString());
        }
        var result = await repository.ReadPriceCatalogSourceAsync("official", default);
        Assert.True(result.InvalidStoredSnapshot); Assert.Null(result.LastGood);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SavePriceCatalogImportAsync(new("official", ApiPriceCatalogTests.At.AddMinutes(1), ApiPriceImportState.Accepted, original, null), default));
    }

    [Fact]
    public async Task ReadOnlyMissingTablesAndAtomicMigrationDoNotRewriteOtherArchives()
    {
        var repository = new SqliteTelemetryRepository(Database); await repository.InitializeAsync(default);
        Assert.Null((await repository.ReadPriceCatalogSourceAsync("official", default)).LastGood);
        await repository.InitializeIntelligenceAsync(default);
        await Execute("DROP TABLE api_price_catalog_snapshots; UPDATE intelligence_schema SET version=9; CREATE VIEW api_price_catalog_snapshots AS SELECT 1;");
        // Force a table operation in the migration to distinguish an obstructing view from a completed table.
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteTelemetryRepository(Database).InitializeIntelligenceAsync(default));
        Assert.Equal(9, await Scalar("SELECT version FROM intelligence_schema"));
        await Execute("DROP VIEW api_price_catalog_snapshots;");
        await new SqliteTelemetryRepository(Database).InitializeIntelligenceAsync(default);
        Assert.Equal(10, await Scalar("SELECT version FROM intelligence_schema"));
    }
}
