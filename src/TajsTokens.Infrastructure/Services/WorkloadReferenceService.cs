using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Core.Services;

namespace TajsTokens.Infrastructure.Services;

/// <summary>Approved first-use selection and explicit imports. No polling or implicit TT refresh.</summary>
public sealed class WorkloadReferenceService(SqliteTelemetryRepository repository)
{
    public const string OfficialSourceKey = "official-imports";
    public const string ApprovedBasisId = "tt-joint:00dac6d4779ceff3507bce2570ac4a7fe30876e53e0fc033d1ae41540a46f6fa";
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private bool _initialized;

    public static async Task<(ApiPriceCatalogSnapshot Prices, TtJointWorkloadBasis Basis)> ReadApprovedAsync(CancellationToken token)
    {
        var assembly = typeof(WorkloadReferenceService).Assembly;
        using var prices = assembly.GetManifestResourceStream("References.ApprovedPrices.json")!;
        using var buffer = new MemoryStream(); await prices.CopyToAsync(buffer, token);
        var attempt = ApiPriceCatalogFileReader.Parse(OfficialSourceKey, buffer.ToArray());
        using var basisStream = assembly.GetManifestResourceStream("References.ApprovedTt.json")!;
        using var json = await JsonDocument.ParseAsync(basisStream, cancellationToken: token);
        var basis = json.RootElement.Deserialize<TtJointWorkloadBasis>()!;
        if (attempt.Snapshot?.SourceArtifactSha256 != "d2a13f4e571b6d72557b08191ee13802e67089f8b676a171eaecfd7cb2914026" ||
            basis.BasisId != ApprovedBasisId || json.RootElement.GetProperty("BasisId").GetString() != basis.BasisId ||
            json.RootElement.GetProperty("ReferenceScore").GetDecimal() != basis.ReferenceScore)
            throw new InvalidOperationException("Approved reference resources differ from the selected artifacts.");
        return (attempt.Snapshot, basis);
    }

    public async Task InitializeAsync(CancellationToken token, bool force = false)
    {
        await _initialization.WaitAsync(token);
        try
        {
            if (_initialized && !force) return;
            var approved = await ReadApprovedAsync(token);
            await repository.InitializeIntelligenceAsync(token);
            var source = await repository.ReadPriceCatalogSourceAsync(OfficialSourceKey, token);
            if (source.LastState is null && !source.InvalidStoredSnapshot)
                await repository.SavePriceCatalogImportAsync(new(OfficialSourceKey, DateTimeOffset.UtcNow,
                    ApiPriceImportState.Accepted, approved.Prices, null), token, onlyIfMissing: true);
            await repository.SaveWorkloadReferenceSelectionAsync(new(DateTimeOffset.UtcNow, OfficialSourceKey, approved.Basis), token, onlyIfMissing: true);
            _initialized = true;
        }
        finally { _initialization.Release(); }
    }

    public async Task<ApiPriceImportAttempt> ImportAsync(string file, ApiPriceOrigin role, CancellationToken token)
    {
        var state = await ReadAsync(token);
        var selected = state.Selection ?? throw new InvalidOperationException("A usable reference selection is required.");
        string key = role switch
        {
            ApiPriceOrigin.OfficialCurated => selected.PriceSourceKey,
            ApiPriceOrigin.LocalOverride => "local-overrides",
            ApiPriceOrigin.CommunityFallback => "selected-fallback",
            _ => throw new ArgumentException("Unsupported import role."),
        };
        var attempt = await ApiPriceCatalogFileReader.ReadAsync(key, file, token);
        if (attempt.Snapshot is { } snapshot && (snapshot.Origin != role || role == ApiPriceOrigin.OfficialCurated && snapshot.ReferenceContext is null))
            attempt = attempt with { State = ApiPriceImportState.Invalid, Snapshot = null, Issue = "price-source-role-mismatch" };
        await repository.SavePriceCatalogImportAsync(attempt, token);
        if (attempt.State == ApiPriceImportState.Accepted && role != ApiPriceOrigin.OfficialCurated)
            await repository.SaveWorkloadReferenceSelectionAsync(selected with
            {
                SelectedAtUtc = DateTimeOffset.UtcNow,
                OverrideSourceKey = role == ApiPriceOrigin.LocalOverride ? key : selected.OverrideSourceKey,
                FallbackSourceKey = role == ApiPriceOrigin.CommunityFallback ? key : selected.FallbackSourceKey,
            }, token, expectedPreviousId: selected.SelectionId);
        return attempt;
    }

    public async Task SelectBasisAsync(string file, CancellationToken token)
    {
        var state = await ReadAsync(token);
        var selected = state.Selection ?? throw new InvalidOperationException("A usable reference selection is required.");
        using var stream = File.OpenRead(file);
        if (stream.Length is <= 0 or > 512 * 1024) throw new ArgumentException("Basis size is unsupported.");
        byte[] bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, token);
        if (stream.ReadByte() != -1) throw new ArgumentException("Basis changed during reading.");
        using var json = JsonDocument.Parse(bytes);
        var basis = json.RootElement.Deserialize<TtJointWorkloadBasis>() ?? throw new ArgumentException("Missing basis.");
        if (json.RootElement.GetProperty("BasisId").GetString() != basis.BasisId ||
            json.RootElement.GetProperty("ReferenceScore").GetDecimal() != basis.ReferenceScore)
            throw new ArgumentException("Basis identity differs from definition.");
        if (basis.BasisId == selected.Basis.BasisId) return;
        if (basis.ParentBasisId is not null && !basis.IsCompatibleExtensionOf(selected.Basis))
            throw new ArgumentException("Extension must preserve the currently selected parent.");
        await repository.SaveWorkloadReferenceSelectionAsync(selected with { SelectedAtUtc = DateTimeOffset.UtcNow, Basis = basis }, token,
            expectedPreviousId: selected.SelectionId);
    }

    public async Task SetOptionalSourcesAsync(bool useOverrides, bool useFallback, bool legacyFallback, CancellationToken token)
    {
        var state = await ReadAsync(token);
        var selected = state.Selection ?? throw new InvalidOperationException("A usable reference selection is required.");
        if (legacyFallback)
            await repository.SavePriceCatalogImportAsync(new("legacy-pinned", DateTimeOffset.UtcNow, ApiPriceImportState.Accepted,
                ApiPriceWorkload.LegacySnapshot(), null), token, onlyIfMissing: true);
        string? fallback = !useFallback ? null : legacyFallback ? "legacy-pinned" : selected.FallbackSourceKey;
        if (useFallback && fallback is null) throw new ArgumentException("Import a fallback first.");
        await repository.SaveWorkloadReferenceSelectionAsync(selected with { SelectedAtUtc = DateTimeOffset.UtcNow,
            OverrideSourceKey = useOverrides ? selected.OverrideSourceKey ?? "local-overrides" : null,
            FallbackSourceKey = fallback }, token, expectedPreviousId: selected.SelectionId);
    }

    public async Task<WorkloadReferenceState> ReadAsync(CancellationToken token, bool initialize = true)
    {
        if (initialize) await InitializeAsync(token);
        var state = await repository.ReadWorkloadReferenceSelectionAsync(token);
        if (initialize && state.Selection is null && state.Issue is null)
        {
            await InitializeAsync(token, force: true);
            state = await repository.ReadWorkloadReferenceSelectionAsync(token);
        }
        if (state.Selection is not { } selected) return state;
        return state with
        {
            Prices = await repository.ReadPriceCatalogSourceAsync(selected.PriceSourceKey, token),
            Overrides = selected.OverrideSourceKey is { } overrides ? await repository.ReadPriceCatalogSourceAsync(overrides, token) : null,
            Fallback = selected.FallbackSourceKey is { } fallback ? await repository.ReadPriceCatalogSourceAsync(fallback, token) : null,
        };
    }
}
