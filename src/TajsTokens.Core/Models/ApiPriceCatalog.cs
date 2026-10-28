using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TajsTokens.Core.Models;

public enum ApiPriceOrigin { OfficialCurated, LocalOverride, CommunityFallback, LegacyPinnedFallback }
public enum ApiPriceTimeMode { SnapshotReference, DateEffective }
public enum ApiPriceImportState { Accepted, Invalid, Unavailable }
public sealed record ApiPriceCoordinate(string Model, string Product, string Tier, string ContextBand);
public sealed record ApiPriceReferenceContext(string Product, string Tier, string ContextBand, string? CacheWritePolicy);
public sealed record ApiPriceRate(ApiPriceCoordinate Coordinate, TtTokenCategory Category, string? CacheWritePolicy,
    decimal? UsdPerMillion, DateTimeOffset? EffectiveFromUtc, DateTimeOffset? EffectiveToUtc, string? SourceUri);

/// <summary>Immutable price data. Importing a snapshot never activates prices, TT weights or a source policy.</summary>
public sealed class ApiPriceCatalogSnapshot
{
    public const string Version = "api-price-catalog/v1";
    public const int MaximumRates = 4096;
    [JsonConstructor]
    public ApiPriceCatalogSnapshot(string contract, ApiPriceOrigin origin, string sourceName, DateTimeOffset? retrievedAtUtc,
        string currency, string rateUnit, IReadOnlyList<string> sourceUris, IReadOnlyList<ApiPriceRate> rates,
        ApiPriceReferenceContext? referenceContext = null, string? sourceArtifactSha256 = null)
    {
        if (contract != Version || !Enum.IsDefined(origin) || !Label(sourceName, 128) || retrievedAtUtc == DateTimeOffset.MinValue ||
            currency != "USD" || rateUnit != "per-1000000-tokens" || sourceUris is null || sourceUris.Count > 32 ||
            origin != ApiPriceOrigin.LocalOverride && sourceUris.Count == 0 || sourceUris.Any(x => !ValidUri(x, origin)) ||
            rates is null || rates.Count is < 1 or > MaximumRates || sourceArtifactSha256 is not null &&
            (sourceArtifactSha256.Length != 64 || sourceArtifactSha256.Any(x => !char.IsAsciiHexDigit(x) || char.IsUpper(x))))
            throw new ArgumentException("Unsupported price snapshot, source, currency or unit.");
        if (referenceContext is not null && (!Label(referenceContext.Product, 128) || !Label(referenceContext.Tier, 128) ||
            !Label(referenceContext.ContextBand, 128) || !Optional(referenceContext.CacheWritePolicy, 128))) throw new ArgumentException("Invalid reference context.");
        Contract = contract; Origin = origin; SourceName = sourceName; RetrievedAtUtc = retrievedAtUtc?.ToUniversalTime();
        Currency = currency; RateUnit = rateUnit; ReferenceContext = referenceContext; SourceArtifactSha256 = sourceArtifactSha256;
        SourceUris = Array.AsReadOnly(sourceUris.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        if (rates.Any(x => x is null || x.Coordinate is null || !Label(x.Coordinate.Model, 256) || !Label(x.Coordinate.Product, 128) ||
            !Label(x.Coordinate.Tier, 128) || !Label(x.Coordinate.ContextBand, 128) || !Enum.IsDefined(x.Category) || x.UsdPerMillion < 0 ||
            (x.Category == TtTokenCategory.CacheWrite ? !Label(x.CacheWritePolicy, 128) : x.CacheWritePolicy is not null) ||
            x.EffectiveFromUtc is { } from && x.EffectiveToUtc is { } until && from >= until ||
            x.SourceUri is not null && !SourceUris.Contains(x.SourceUri, StringComparer.Ordinal))) throw new ArgumentException("Invalid price coordinate, amount, source or effective interval.");
        Rates = Array.AsReadOnly(rates.Select(x => x with { EffectiveFromUtc = x.EffectiveFromUtc?.ToUniversalTime(), EffectiveToUtc = x.EffectiveToUtc?.ToUniversalTime() })
            .OrderBy(x => Key(x), StringComparer.Ordinal).ThenBy(x => x.EffectiveFromUtc).ToArray());
        foreach (var group in Rates.GroupBy(Key, StringComparer.Ordinal))
        {
            ApiPriceRate? previous = null;
            foreach (var rate in group)
            {
                if (previous is not null && (previous.EffectiveToUtc is null || rate.EffectiveFromUtc is null || previous.EffectiveToUtc > rate.EffectiveFromUtc))
                    throw new ArgumentException("Overlapping or duplicate price intervals.");
                previous = rate;
            }
        }
        _rates = Rates.GroupBy(Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        string canonical = JsonSerializer.Serialize(new { Contract, Origin, SourceName, RetrievedAtUtc, Currency, RateUnit, SourceUris, ReferenceContext, SourceArtifactSha256,
            Rates = Rates.Select(x => new { x.Coordinate, x.Category, x.CacheWritePolicy, UsdPerMillion = x.UsdPerMillion?.ToString("G29", CultureInfo.InvariantCulture), x.EffectiveFromUtc, x.EffectiveToUtc, x.SourceUri }) });
        SnapshotId = "api-prices:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
    private readonly Dictionary<string, ApiPriceRate[]> _rates;
    public string Contract { get; }
    public string SnapshotId { get; }
    public ApiPriceOrigin Origin { get; }
    public string SourceName { get; }
    public DateTimeOffset? RetrievedAtUtc { get; }
    public string Currency { get; }
    public string RateUnit { get; }
    public IReadOnlyList<string> SourceUris { get; }
    public IReadOnlyList<ApiPriceRate> Rates { get; }
    public ApiPriceReferenceContext? ReferenceContext { get; }
    public string? SourceArtifactSha256 { get; }
    public ApiPriceRate? Find(ApiPriceCoordinate coordinate, TtTokenCategory category, string? cacheWritePolicy,
        ApiPriceTimeMode mode, DateTimeOffset? verifiedEventAtUtc, DateTimeOffset referenceAtUtc)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Unsupported price time mode.");
        if (mode == ApiPriceTimeMode.DateEffective && verifiedEventAtUtc is null) return null;
        string key = Key(new(coordinate, category, category == TtTokenCategory.CacheWrite ? cacheWritePolicy : null, null, null, null, null));
        if (!_rates.TryGetValue(key, out var rates)) return null;
        DateTimeOffset at = mode == ApiPriceTimeMode.DateEffective ? verifiedEventAtUtc!.Value : referenceAtUtc;
        return rates.SingleOrDefault(x => (mode != ApiPriceTimeMode.DateEffective || x.EffectiveFromUtc is not null) &&
            (x.EffectiveFromUtc is null || x.EffectiveFromUtc <= at) && (x.EffectiveToUtc is null || at < x.EffectiveToUtc));
    }
    private static string Key(ApiPriceRate rate) => JsonSerializer.Serialize(new { rate.Coordinate, rate.Category, rate.CacheWritePolicy });
    private static bool Label(string? value, int bound) => !string.IsNullOrWhiteSpace(value) && value.Length <= bound;
    private static bool Optional(string? value, int bound) => value is null || Label(value, bound);
    private static bool ValidUri(string value, ApiPriceOrigin origin) => value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        (origin != ApiPriceOrigin.OfficialCurated || uri.Host is "developers.openai.com" or "platform.openai.com" or "openai.com");
}

public sealed record ApiPriceImportAttempt(string SourceKey, DateTimeOffset AttemptedAtUtc, ApiPriceImportState State, ApiPriceCatalogSnapshot? Snapshot, string? Issue);
public sealed record ApiPriceCatalogSource(string SourceKey, ApiPriceImportState? LastState, DateTimeOffset? LastAttemptAtUtc,
    ApiPriceCatalogSnapshot? LastGood, DateTimeOffset? FirstImportedAtUtc, bool InvalidStoredSnapshot);
public sealed record ApiPriceCatalogArchiveEntry(string SnapshotId, DateTimeOffset? ImportedAtUtc, ApiPriceCatalogSnapshot? Snapshot, bool Invalid);
public sealed record ApiPriceWorkloadItem(string? Model, decimal UncachedInput, decimal CacheRead, decimal CacheWrite,
    decimal NonReasoningOutput, decimal ReasoningOutput, decimal ReportedTotal, DateTimeOffset? VerifiedEventAtUtc = null, bool SourceInconsistent = false);
public enum ApiPriceValuationState { Complete, Partial, Inconsistent, ArithmeticUnavailable }
public sealed record ApiPriceContribution(string SnapshotId, ApiPriceOrigin Origin, string SourceName, decimal AmountUsd, decimal Tokens);
public sealed record ApiPriceValuation(string Contract, ApiPriceTimeMode Mode, ApiPriceReferenceContext Context, DateTimeOffset CalculatedAtUtc,
    ApiPriceValuationState State, decimal? FullAmountUsd, decimal? CoveredAmountUsd, decimal? PricedTokens, decimal? UnpricedTokens,
    int InconsistentRows, bool UsesStaleSource, bool HasFutureSourceTime, bool HasUnknownSourceTime, IReadOnlyList<ApiPriceContribution> Contributions);
