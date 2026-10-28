using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TajsTokens.Core.Models;

/// <summary>Explicit selection event. Prices can advance within a source; the embedded TT definition cannot.</summary>
public sealed record WorkloadReferenceSelection(DateTimeOffset SelectedAtUtc, string PriceSourceKey,
    TtJointWorkloadBasis Basis, string? OverrideSourceKey = null, string? FallbackSourceKey = null)
{
    public const string Version = "workload-reference-selection/v1";
    public string Contract { get; init; } = Version;
    public string SelectionId => "reference:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Contract, SelectedAtUtc = SelectedAtUtc.ToUniversalTime(), PriceSourceKey,
            BasisId = Basis.BasisId, OverrideSourceKey, FallbackSourceKey }))));
    public bool IsValid => Contract == Version && SelectedAtUtc != default && Basis is not null && ValidKey(PriceSourceKey) &&
        (OverrideSourceKey is null || ValidKey(OverrideSourceKey)) && (FallbackSourceKey is null || ValidKey(FallbackSourceKey));
    private static bool ValidKey(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

public sealed record WorkloadReferenceState(WorkloadReferenceSelection? Selection, ApiPriceCatalogSource? Prices,
    ApiPriceCatalogSource? Overrides = null, ApiPriceCatalogSource? Fallback = null, string? Issue = null);

public sealed record WorkloadReferenceResult(WorkloadReferenceState Sources, TtScoreAssessment? Tt,
    ApiPriceValuation? CurrentPrice, ApiPriceValuation? DateEffectivePrice)
{
    public const int StaleAfterDays = 30;
    public string TtSummary => Tt?.State switch
    {
        TtScoreState.Exact => $"{Tt.ExactTt:N2} TT",
        TtScoreState.FiniteBounds => $"{Tt.LowerTt:N2}–{Tt.UpperTt:N2} TT · bounds",
        TtScoreState.Partial => $"{Tt.CoveredSubtotalTt:N2} TT · covered portion",
        null => "TT unavailable",
        _ => $"TT unavailable · {Tt.State}",
    };
    public string BasisLabel => Sources.Selection is { } selected
        ? $"Frozen {selected.Basis.Vintage} · {Tt?.KnownUnmappedTokens?.ToString("N0") ?? "unknown"} unmapped tokens · local evidence"
        : "No usable selected TT basis";
    public string PriceSummary => CurrentPrice is not { } price ? "API reference unavailable" : price.State switch
    {
        ApiPriceValuationState.Complete => $"API reference ${price.FullAmountUsd:N2}" + PriceAgeLabel,
        ApiPriceValuationState.Partial when price.PricedTokens > 0 => $"API reference ${price.CoveredAmountUsd:N2} · priced portion" + PriceAgeLabel,
        _ => $"API reference unavailable · {price.State}",
    };
    private string PriceAgeLabel =>
        (CurrentPrice?.Contributions.Any(x => x.Origin == ApiPriceOrigin.LocalOverride) == true ? " · override applied" : "") +
        (CurrentPrice?.Contributions.Any(x => x.Origin is ApiPriceOrigin.CommunityFallback or ApiPriceOrigin.LegacyPinnedFallback) == true ? " · fallback applied" : "") +
        (CurrentPrice?.UsesStaleSource == true ? " · stale prices" :
            CurrentPrice?.HasFutureSourceTime == true ? " · future source clock" :
            CurrentPrice?.HasUnknownSourceTime == true ? " · source age unknown" : " · snapshot reference") +
        (Sources.Prices?.LastState is ApiPriceImportState.Invalid or ApiPriceImportState.Unavailable ? " · last import failed" : "");
}
