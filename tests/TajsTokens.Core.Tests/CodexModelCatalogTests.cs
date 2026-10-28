using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexModelCatalogTests : IDisposable
{
    private readonly string _directory;
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
    public CodexModelCatalogTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx"))) root = root.Parent;
        _directory = Path.Combine(root!.FullName, ".codex", "temp", "model-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }
    private string Database => Path.Combine(_directory, "telemetry.db");
    private string Cache => Path.Combine(_directory, "models_cache.json");
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
    private static CodexModelMetadata Metadata(string id) => new(id, id, [], ["high", "low"], "high", ["text"], ["priority"], 100, 200, true, "list", null, null, ["supports_search_tool"]);
    private static CodexModelCatalogDefinition Definition(params CodexModelMetadata[] models) => new(CodexModelCatalogDefinition.Version, "codex-home/models_cache.json", "fixture-v1", models);
    private static CodexModelCatalogRead Read(CodexModelCatalogDefinition definition, int minutes = 0) => new(CodexModelCatalogReadState.Available, At.AddMinutes(minutes), At.AddMinutes(minutes), At.AddDays(-1).AddMinutes(minutes), definition, null);
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
    public async Task NativeCacheProjectionIsReadOnlyAndNeverRetainsIdentityInstructionsOrUnknownPayloads()
    {
        await File.WriteAllTextAsync(Cache, """
            {"fetched_at":"2026-10-01T00:00:00Z","client_version":"0.164.0","identity":"SECRET-IDENTITY","models":[{
            "slug":"new-model","display_name":"New model","description":"DO-NOT-RETAIN-DESCRIPTION",
            "model_messages":{"base_instructions":"DO-NOT-RETAIN-INSTRUCTIONS"},"future_payload":{"credential":"SECRET-CREDENTIAL"},
            "supported_reasoning_levels":[{"effort":"high","description":"DO-NOT-RETAIN-EFFORT-TEXT"}],"default_reasoning_level":"high",
            "input_modalities":["text","image"],"service_tiers":[{"id":"priority","description":"DO-NOT-RETAIN-TIER-TEXT"}],
            "context_window":1000,"max_context_window":2000,"visibility":"hide","supported_in_api":true,
            "supports_search_tool":true,"upgrade":{"id":"successor","retirement_at":"2030-01-01T00:00:00Z","migration_markdown":"DO-NOT-RETAIN-MIGRATION"}}]}
            """);
        byte[] before = await File.ReadAllBytesAsync(Cache); DateTime modified = File.GetLastWriteTimeUtc(Cache);
        var result = await new CodexNativeSourcesService(_directory).ReadModelsAsync();
        Assert.Equal(CodexModelCatalogReadState.Available, result.State);
        var metadata = Assert.Single(result.Definition!.Models);
        Assert.Equal("new-model", metadata.Model); Assert.Equal("successor", metadata.SuccessorModel);
        Assert.Equal(DateTimeOffset.Parse("2030-01-01T00:00:00Z"), metadata.ReportedRetirementAtUtc);
        Assert.Empty(metadata.Aliases); Assert.Equal(["high"], metadata.ReasoningEfforts); Assert.Equal(["priority"], metadata.ServiceTiers);
        Assert.Equal(1000, metadata.ContextWindow); Assert.True(metadata.ListedForApi);
        string projected = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("SECRET", projected); Assert.DoesNotContain("DO-NOT-RETAIN", projected); Assert.DoesNotContain("identity", projected);
        Assert.Equal(before, await File.ReadAllBytesAsync(Cache)); Assert.Equal(modified, File.GetLastWriteTimeUtc(Cache));
        await File.WriteAllTextAsync(Cache, "{}");
        Assert.Equal(CodexModelCatalogReadState.Invalid, (await new CodexNativeSourcesService(_directory).ReadModelsAsync()).State);
    }

    [Fact]
    public void DefinitionIdentityIsCanonicalImmutableAndDoesNotConfuseAnAliasWithSuccessorOrAccess()
    {
        string[] efforts = ["low", "high", "low"];
        var metadata = Metadata("m") with { ReasoningEfforts = efforts, Aliases = ["published-alias"], SuccessorModel = "successor" };
        var definition = Definition(metadata, Metadata("other"));
        var same = Definition(Metadata("other"), metadata with { ReasoningEfforts = ["high", "low"] });
        Assert.Equal(definition.DefinitionId, same.DefinitionId); efforts[0] = "mutated";
        Assert.DoesNotContain("mutated", definition.Models.Single(x => x.Model == "m").ReasoningEfforts);
        Assert.NotEqual(definition.DefinitionId, Definition(metadata with { ContextWindow = 999 }, Metadata("other")).DefinitionId);
        Assert.Equal(definition.DefinitionId, JsonSerializer.Deserialize<CodexModelCatalogDefinition>(JsonSerializer.Serialize(definition))!.DefinitionId);
        Assert.Throws<ArgumentException>(() => Definition(metadata, metadata));
    }

    [Fact]
    public async Task VersionsPreserveFirstCollectionRetiredMetadataAndLastGoodWithoutRestampingOrModelGate()
    {
        var repository = new SqliteTelemetryRepository(Database);
        var old = Definition(Metadata("retired") with { ReportedRetirementAtUtc = At.AddYears(1), SuccessorModel = "current" }, Metadata("quiet-old-model"));
        await repository.SaveModelCatalogReadAsync(Read(old), default);
        await repository.SaveModelCatalogReadAsync(Read(old, 1), default);
        var current = Definition(Metadata("current")); await repository.SaveModelCatalogReadAsync(Read(current, 2), default);
        await repository.SaveModelCatalogReadAsync(new(CodexModelCatalogReadState.Invalid, At.AddMinutes(3), At.AddMinutes(3), null, null, "fixture-failure"), default);
        var history = await new SqliteTelemetryRepository(Database).ReadModelCatalogHistoryAsync(default);
        Assert.Equal(2, history.Versions.Count); Assert.Equal(CodexModelCatalogReadState.Invalid, history.LastRead!.State);
        var savedOld = history.Versions.Single(x => x.Definition.DefinitionId == old.DefinitionId);
        Assert.Equal(At, savedOld.FirstCollectedAtUtc); Assert.Equal(At.AddMinutes(1), savedOld.LastCollectedAtUtc);
        Assert.Equal(At.AddDays(-1), savedOld.FirstSourceFetchedAtUtc); Assert.Equal(At.AddDays(-1).AddMinutes(1), savedOld.LastSourceFetchedAtUtc);
        var unknown = new CodexPredictiveTokenEvent("s", At, At, "never-listed", "future-effort", 1, 0, 0, 0, 0, 1);
        var selection = new[] { new CodexLedgerEntry("e", "source", "t", "p", At, At, null, AccountEvidenceClass.Unattributed, unknown) };
        var choices = CodexModelCatalogProjection.Build(history, selection).Choices;
        Assert.True(choices.Single(x => x.Model == "current").ListedInRetainedCatalog);
        Assert.False(choices.Single(x => x.Model == "retired").ListedInRetainedCatalog);
        Assert.NotNull(choices.Single(x => x.Model == "retired").Metadata!.ReportedRetirementAtUtc);
        Assert.Null(choices.Single(x => x.Model == "quiet-old-model").Metadata!.ReportedRetirementAtUtc); // absence is not retirement.
        var observed = choices.Single(x => x.Model == "never-listed"); Assert.True(observed.ObservedInSelection);
        Assert.Equal(["future-effort"], observed.ObservedEfforts); Assert.Null(observed.Metadata);
        Assert.All(choices, x => Assert.Equal("Unverified", x.AccountAccess));
        Assert.Equal(1, unknown.ReportedTotalTokens);
    }

    [Fact]
    public async Task CorruptedVersionIsSkippedWithoutHidingOtherVersionsOrUnknownObservedNames()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.SaveModelCatalogReadAsync(Read(Definition(Metadata("first"))), default);
        await repository.SaveModelCatalogReadAsync(Read(Definition(Metadata("second")), 1), default);
        string payload = JsonSerializer.Serialize(Definition(Metadata("second"))).Replace("codex-models:", "tampered-models:");
        await Execute("UPDATE codex_model_catalog_definitions SET payload=$payload,content_hash=$hash WHERE last_collected_at_utc=(SELECT MAX(last_collected_at_utc) FROM codex_model_catalog_definitions)", payload);
        var result = await repository.ReadModelCatalogHistoryAsync(default);
        Assert.True(result.IsPartial); Assert.Equal(1, result.InvalidVersions); Assert.Single(result.Versions);
    }

    [Fact]
    public async Task MigrationFailureDoesNotAdvanceSchemaAndReadOnlyHistoryDoesNotInitializeMissingTables()
    {
        var repository = new SqliteTelemetryRepository(Database); await repository.InitializeAsync(default);
        Assert.Empty((await repository.ReadModelCatalogHistoryAsync(default)).Versions);
        await repository.InitializeIntelligenceAsync(default);
        await Execute("DROP TABLE codex_model_catalog_definitions; UPDATE intelligence_schema SET version=8; CREATE VIEW codex_model_catalog_definitions AS SELECT 1;");
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteTelemetryRepository(Database).InitializeIntelligenceAsync(default));
        Assert.Equal(8, await Scalar("SELECT version FROM intelligence_schema"));
        await Execute("DROP VIEW codex_model_catalog_definitions;");
        await new SqliteTelemetryRepository(Database).InitializeIntelligenceAsync(default);
        Assert.Equal(10, await Scalar("SELECT version FROM intelligence_schema"));
    }

    [Fact]
    public async Task CatalogCollectionSucceedsLocallyEvenWhenServerCollectionFailsAndDoesNotRepeatAtQuotaCadence()
    {
        var repository = new SqliteTelemetryRepository(Database); int reads = 0;
        var service = new CodexServerEvidenceService(Database, repository, new FailingProvider(), modelCatalogReader: _ =>
        { reads++; return Task.FromResult(Read(Definition(Metadata("local-model")))); });
        await service.CollectAsync(false, default); await service.CollectAsync(false, default);
        Assert.Equal(1, reads); Assert.Single((await repository.ReadModelCatalogHistoryAsync(default)).Versions);
    }
    private sealed class FailingProvider : ICodexServerEvidenceProvider
    {
        public Task<CodexServerCollection> CollectAsync(IReadOnlyList<string> threadIds, CancellationToken cancellationToken) => throw new IOException("fixture");
    }
}
