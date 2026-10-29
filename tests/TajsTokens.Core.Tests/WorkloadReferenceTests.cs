using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class WorkloadReferenceTests : IDisposable
{
    private readonly string _directory;
    public WorkloadReferenceTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx"))) root = root.Parent;
        _directory = Path.Combine(root!.FullName, ".codex", "temp", "reference-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }
    private string Database => Path.Combine(_directory, "telemetry.db");
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
    private async Task<long> Execute(string sql, bool scalar = false)
    {
        using var connection = new SqliteConnection("Data Source=" + Database); await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return scalar ? (long)(await command.ExecuteScalarAsync())! : await command.ExecuteNonQueryAsync();
    }
    private static CodexLedgerEntry Row(string model = "gpt-6.1-sol", long input = 1_000_000) =>
        new("event-" + model, null, "thread", "project", DateTimeOffset.Parse("2026-10-27T12:00:00Z"),
            DateTimeOffset.Parse("2026-10-28T12:00:00Z"), null, AccountEvidenceClass.Unattributed,
            new("session", DateTimeOffset.Parse("2026-10-27T12:00:00Z"), null, model, null, input, 0, 0, 0, 0, input));
    private static WorkloadReferenceResult Project(WorkloadReferenceState state, params CodexLedgerEntry[] rows) =>
        WorkloadReferenceProjection.Build(state, CodexJointWorkloadBuilder.Build(rows.Select(x => x.Workload)), rows,
            DateTimeOffset.Parse("2026-10-29T12:00:00Z"));

    [Fact]
    public async Task ApprovedBasketScoresOneAndIndependentPricesKeepUnknownEffectiveDates()
    {
        var approved = await WorkloadReferenceService.ReadApprovedAsync(default);
        var selected = new WorkloadReferenceSelection(DateTimeOffset.UtcNow, "official", approved.Basis);
        var state = new WorkloadReferenceState(selected, new("official", ApiPriceImportState.Accepted, DateTimeOffset.UtcNow, approved.Prices, DateTimeOffset.UtcNow, false));
        var result = Project(state, Row());
        Assert.Equal(WorkloadReferenceService.ApprovedBasisId, selected.Basis.BasisId);
        Assert.Equal(1, result.Tt!.ExactTt); Assert.Equal(2, result.CurrentPrice!.FullAmountUsd);
        Assert.Null(result.DateEffectivePrice!.FullAmountUsd); Assert.Equal(1_000_000, result.DateEffectivePrice.UnpricedTokens);
        Assert.Contains("Frozen", result.BasisLabel); Assert.DoesNotContain("stale", result.PriceSummary);
        var partial = Project(state, Row(), Row("unknown"));
        Assert.Equal(TtScoreState.Partial, partial.Tt!.State); Assert.Equal(1, partial.Tt.CoveredSubtotalTt);
        Assert.Equal(1_000_000, partial.Tt.KnownUnmappedTokens);
        Assert.Null(partial.CurrentPrice!.FullAmountUsd); Assert.Equal(2, partial.CurrentPrice.CoveredAmountUsd);
        Assert.Contains("covered portion", partial.TtSummary);
        var stale = WorkloadReferenceProjection.Build(state, CodexJointWorkloadBuilder.Build([Row().Workload]), [Row()],
            approved.Prices.RetrievedAtUtc!.Value.AddDays(31));
        Assert.Contains("stale prices", stale.PriceSummary); Assert.Equal(result.Tt, stale.Tt);
    }

    [Fact]
    public async Task RestartAndConcurrentFirstUsePreserveSelectionsAndNewPriceImportsNeverChangeTt()
    {
        var repository = new SqliteTelemetryRepository(Database); await repository.InitializeAsync(default);
        var first = new WorkloadReferenceService(repository);
        await Task.WhenAll(first.InitializeAsync(default), new WorkloadReferenceService(new(Database)).InitializeAsync(default));
        var original = await first.ReadAsync(default);
        Assert.Equal(1, await Execute("SELECT COUNT(*) FROM workload_reference_selections", true));
        var prices = original.Prices!.LastGood!;
        var updated = new ApiPriceCatalogSnapshot(prices.Contract, prices.Origin, "new official snapshot", prices.RetrievedAtUtc,
            prices.Currency, prices.RateUnit, prices.SourceUris, prices.Rates.Select(r => r with { UsdPerMillion = r.UsdPerMillion * 2 }).ToArray(), prices.ReferenceContext);
        string file = Path.Combine(_directory, "new.json"); await File.WriteAllTextAsync(file, JsonSerializer.Serialize(updated));
        Assert.Equal(ApiPriceImportState.Accepted, (await first.ImportAsync(file, ApiPriceOrigin.OfficialCurated, default)).State);
        var restarted = await new WorkloadReferenceService(new(Database)).ReadAsync(default);
        Assert.Equal(original.Selection!.SelectionId, restarted.Selection!.SelectionId);
        Assert.Equal(updated.SnapshotId, restarted.Prices!.LastGood!.SnapshotId);
        Assert.Equal(4, Project(restarted, Row()).CurrentPrice!.FullAmountUsd);
        Assert.Equal(1, Project(restarted, Row()).Tt!.ExactTt);
        Assert.Equal(2, await Execute("SELECT COUNT(*) FROM api_price_catalog_snapshots", true));
        Assert.Equal(0, await Execute("SELECT COUNT(*) FROM tt_joint_score_snapshots", true));
    }

    [Fact]
    public async Task FailedAndWrongRoleImportsRetainLastGoodAndExplicitOverridesCanBeDisabled()
    {
        var repository = new SqliteTelemetryRepository(Database); await repository.InitializeAsync(default);
        var service = new WorkloadReferenceService(repository); var original = await service.ReadAsync(default);
        string file = Path.Combine(_directory, "input.json"); await File.WriteAllTextAsync(file, "{\"Contract\":\"unknown\"}");
        Assert.Equal(ApiPriceImportState.Invalid, (await service.ImportAsync(file, ApiPriceOrigin.OfficialCurated, default)).State);
        var failed = await service.ReadAsync(default);
        Assert.Equal(original.Prices!.LastGood!.SnapshotId, failed.Prices!.LastGood!.SnapshotId);
        Assert.Equal(original.Selection!.SelectionId, failed.Selection!.SelectionId);
        var primary = original.Prices.LastGood;
        var zero = new ApiPriceCatalogSnapshot(primary.Contract, ApiPriceOrigin.LocalOverride, "explicit zero", null,
            primary.Currency, primary.RateUnit, [], primary.Rates.Select(r => r with { UsdPerMillion = 0, SourceUri = null }).ToArray(), primary.ReferenceContext);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(zero));
        Assert.Equal(ApiPriceImportState.Invalid, (await service.ImportAsync(file, ApiPriceOrigin.OfficialCurated, default)).State);
        Assert.Equal(ApiPriceImportState.Accepted, (await service.ImportAsync(file, ApiPriceOrigin.LocalOverride, default)).State);
        var overridden = Project(await service.ReadAsync(default), Row());
        Assert.Equal(0, overridden.CurrentPrice!.FullAmountUsd); Assert.Equal(1, overridden.Tt!.ExactTt);
        Assert.Equal(ApiPriceOrigin.LocalOverride, Assert.Single(overridden.CurrentPrice.Contributions).Origin);
        await service.SetOptionalSourcesAsync(false, false, false, default);
        Assert.Equal(2, Project(await service.ReadAsync(default), Row()).CurrentPrice!.FullAmountUsd);
        await service.SetOptionalSourcesAsync(false, true, true, default);
        var legacy = Project(await service.ReadAsync(default), Row("gpt-5.6-sol"));
        Assert.Equal(ApiPriceOrigin.LegacyPinnedFallback, Assert.Single(legacy.CurrentPrice!.Contributions).Origin);
        Assert.True(legacy.CurrentPrice.HasUnknownSourceTime); Assert.Equal(TtScoreState.Partial, legacy.Tt!.State);
    }

    [Fact]
    public async Task ExplicitBasisExtensionPreservesOriginalArchiveAndCorruptSelectionDoesNotReselectDefaults()
    {
        var repository = new SqliteTelemetryRepository(Database); await repository.InitializeAsync(default);
        var service = new WorkloadReferenceService(repository); var state = await service.ReadAsync(default);
        var basis = state.Selection!.Basis; var joint = CodexJointWorkloadBuilder.Build([Row().Workload]);
        var original = new TtJointScoreSnapshot("original", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-1), "codex", "default",
            Row().ObservedAtUtc, Row().ObservedAtUtc.AddDays(1), basis, joint, basis.ScoreExact(joint))
            { Assessment = TtJointScoring.Evaluate(basis, joint) };
        await repository.SaveTtJointScoreAsync(original, default);
        var extension = basis.Extend([new(new("added", null), TtTokenCategory.UncachedInput, 3)], "explicit-extension");
        string file = Path.Combine(_directory, "basis.json"); await File.WriteAllTextAsync(file, JsonSerializer.Serialize(extension));
        await service.SelectBasisAsync(file, default);
        var restarted = await new WorkloadReferenceService(new(Database)).ReadAsync(default);
        Assert.Equal(extension.BasisId, restarted.Selection!.Basis.BasisId);
        Assert.Equal(basis.BasisId, Assert.Single(await repository.GetTtJointScoreHistoryAsync("codex", "default", 20, default)).Snapshot!.Basis.BasisId);
        await Execute("UPDATE workload_reference_selections SET content_hash='corrupt' WHERE selection_id=(SELECT selection_id FROM workload_reference_selections ORDER BY selected_at_utc DESC LIMIT 1)");
        var corrupt = await new WorkloadReferenceService(new(Database)).ReadAsync(default);
        Assert.Null(corrupt.Selection); Assert.Equal("invalid-reference-selection", corrupt.Issue);
        Assert.Null(Project(corrupt, Row()).Tt); Assert.Equal(2, await Execute("SELECT COUNT(*) FROM workload_reference_selections", true));
    }

    [Fact]
    public async Task ConcurrentPriceSelectionCannotRollBackANewerExplicitTtBasisEvenWhenWallClockMovesBack()
    {
        var repository = new SqliteTelemetryRepository(Database); await repository.InitializeAsync(default);
        var service = new WorkloadReferenceService(repository); var state = await service.ReadAsync(default);
        var prior = state.Selection!;
        var extension = prior.Basis.Extend([new(new("new-model", null), TtTokenCategory.UncachedInput, 7)], "new-support");
        var newer = prior with { SelectedAtUtc = prior.SelectedAtUtc.AddSeconds(-1), Basis = extension };
        await repository.SaveWorkloadReferenceSelectionAsync(newer, default, expectedPreviousId: prior.SelectionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveWorkloadReferenceSelectionAsync(
            prior with { SelectedAtUtc = DateTimeOffset.UtcNow, OverrideSourceKey = "override" }, default, expectedPreviousId: prior.SelectionId));
        var retained = await service.ReadAsync(default);
        Assert.Equal(extension.BasisId, retained.Selection!.Basis.BasisId);
        Assert.Null(retained.Selection.OverrideSourceKey); Assert.Equal(newer.SelectedAtUtc, retained.Selection.SelectedAtUtc);
    }

    [Fact]
    public async Task ReferenceMigrationRollsBackAndRepairPreservesPriceArchive()
    {
        var repository = new SqliteTelemetryRepository(Database); await repository.InitializeAsync(default);
        await new WorkloadReferenceService(repository).InitializeAsync(default);
        await Execute("DROP TABLE workload_reference_selections; UPDATE intelligence_schema SET version=10; CREATE VIEW workload_reference_selections AS SELECT 1;");
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteTelemetryRepository(Database).InitializeIntelligenceAsync(default));
        Assert.Equal(10, await Execute("SELECT version FROM intelligence_schema", true));
        Assert.Equal(1, await Execute("SELECT COUNT(*) FROM api_price_catalog_snapshots", true));
        await Execute("DROP VIEW workload_reference_selections");
        await new WorkloadReferenceService(new(Database)).InitializeAsync(default);
        Assert.Equal(12, await Execute("SELECT version FROM intelligence_schema", true));
        Assert.Equal(1, await Execute("SELECT COUNT(*) FROM api_price_catalog_snapshots", true));
    }

    [Fact]
    public async Task LiveReferenceCachePublishesBeforeNotificationAndOnlyForChangedSelectionOrSourceState()
    {
        var repository = new SqliteTelemetryRepository(Database); await repository.InitializeAsync(default);
        var service = new WorkloadReferenceService(repository); int events = 0;
        service.Updated += () => { Assert.NotNull(service.Current); Interlocked.Increment(ref events); };
        Assert.Null(service.Current);
        var initial = await service.ReadAsync(default);
        Assert.Equal(initial, service.Current); Assert.Equal(1, events);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.ReadAsync(default)));
        Assert.Equal(1, events);
        string invalid = Path.Combine(_directory, "bad-live.json"); await File.WriteAllTextAsync(invalid, "{}");
        await service.ImportAsync(invalid, ApiPriceOrigin.OfficialCurated, default);
        Assert.Equal(ApiPriceImportState.Invalid, service.Current!.Prices!.LastState);
        Assert.Equal(initial.Selection!.Basis.BasisId, service.Current.Selection!.Basis.BasisId); Assert.Equal(2, events);
        var extension = initial.Selection.Basis.Extend([new(new("future", null), TtTokenCategory.UncachedInput, 2)], "fixture");
        string file = Path.Combine(_directory, "live-basis.json"); await File.WriteAllTextAsync(file, JsonSerializer.Serialize(extension));
        await service.SelectBasisAsync(file, default);
        Assert.Equal(extension.BasisId, service.Current.Selection!.Basis.BasisId); Assert.Equal(3, events);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(cancelled.Token));
        Assert.Equal(extension.BasisId, service.Current.Selection.Basis.BasisId); Assert.Equal(3, events);
    }
}
