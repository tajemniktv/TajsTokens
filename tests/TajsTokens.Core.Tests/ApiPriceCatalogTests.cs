using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class ApiPriceCatalogTests
{
    internal static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
    internal static readonly ApiPriceCoordinate Coordinate = new("fixture-model", "text-reference", "standard", "short");
    internal static readonly ApiPriceReferenceContext Context = new("text-reference", "standard", "short", "30m");
    internal static readonly string[] Sources = ["https://developers.openai.com/api/docs/pricing"];
    internal static ApiPriceRate Rate(decimal? price = 2, TtTokenCategory category = TtTokenCategory.UncachedInput,
        DateTimeOffset? from = null, DateTimeOffset? until = null) => new(Coordinate, category, category == TtTokenCategory.CacheWrite ? "30m" : null, price, from, until, Sources[0]);
    internal static ApiPriceCatalogSnapshot Snapshot(ApiPriceOrigin origin = ApiPriceOrigin.OfficialCurated, params ApiPriceRate[] rates) =>
        new(ApiPriceCatalogSnapshot.Version, origin, "fixture source", At, "USD", "per-1000000-tokens", Sources, rates.Length == 0 ? [Rate()] : rates, Context);
    private static ApiPriceValuation Quote(ApiPriceCatalogSnapshot snapshot, ApiPriceWorkloadItem row,
        ApiPriceTimeMode mode = ApiPriceTimeMode.SnapshotReference, ApiPriceReferenceContext? context = null,
        ApiPriceCatalogSnapshot? overrides = null, ApiPriceCatalogSnapshot? fallback = null, bool allowFallback = false, DateTimeOffset? at = null) =>
        ApiPriceCatalogValuation.Calculate(snapshot, [row], context ?? Context, mode, at ?? At, TimeSpan.FromDays(30), overrides, fallback, allowFallback);
    private static ApiPriceWorkloadItem Work(decimal input = 1_000_000, decimal output = 0) => new(Coordinate.Model, input, 0, 0, output, 0, input + output);

    [Fact]
    public void SnapshotIdentityIsCanonicalImmutableAndIncludesOriginUnitsAndSourceProvenance()
    {
        var rows = new[] { Rate(2), Rate(10, TtTokenCategory.NonReasoningOutput) };
        var original = Snapshot(ApiPriceOrigin.OfficialCurated, rows);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            var same = Snapshot(ApiPriceOrigin.OfficialCurated, rows.Reverse().Select(x => x with { UsdPerMillion = x.UsdPerMillion * 1.00m }).ToArray());
            Assert.Equal(original.SnapshotId, same.SnapshotId);
            rows[0] = rows[0] with { UsdPerMillion = 999 };
            Assert.Equal(2, original.Rates.Single(x => x.Category == TtTokenCategory.UncachedInput).UsdPerMillion);
            Assert.Equal(original.SnapshotId, JsonSerializer.Deserialize<ApiPriceCatalogSnapshot>(JsonSerializer.Serialize(original))!.SnapshotId);
            Assert.NotEqual(original.SnapshotId, Snapshot(ApiPriceOrigin.LocalOverride, original.Rates.ToArray()).SnapshotId);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void SchemaRejectsWrongCurrencyUnitsNegativeRatesFalseOfficialSourcesAndOverlappingEpochs()
    {
        ApiPriceCatalogSnapshot Make(string currency = "USD", string unit = "per-1000000-tokens", string[]? sources = null, ApiPriceRate[]? rates = null) =>
            new(ApiPriceCatalogSnapshot.Version, ApiPriceOrigin.OfficialCurated, "fixture", At, currency, unit, sources ?? Sources, rates ?? [Rate()]);
        Assert.Throws<ArgumentException>(() => Make(currency: "EUR")); Assert.Throws<ArgumentException>(() => Make(unit: "per-token"));
        Assert.Throws<ArgumentException>(() => Make(sources: ["https://third-party.example/prices"]));
        Assert.Throws<ArgumentException>(() => Make(sources: ["https://developers.openai.com/api/docs/pricing?token=secret"]));
        Assert.Throws<ArgumentException>(() => Make(rates: [Rate(-1)])); Assert.Throws<ArgumentException>(() => Make(rates: [Rate(), Rate()]));
        Assert.Throws<ArgumentException>(() => Make(rates: [Rate(2, from: At, until: At.AddDays(2)), Rate(3, from: At.AddDays(1))]));
        Assert.Throws<ArgumentException>(() => Make(rates: [Rate() with { CacheWritePolicy = "30m" }]));
        Assert.Throws<ArgumentException>(() => Make(rates: [Rate(category: TtTokenCategory.CacheWrite) with { CacheWritePolicy = null }]));
    }

    [Fact]
    public void UndatedSnapshotReferenceDoesNotManufactureHistoricalPricesAndEpochBoundariesAreHalfOpen()
    {
        var undated = Snapshot(); var row = Work() with { VerifiedEventAtUtc = At.AddDays(-10) };
        Assert.Equal(2, Quote(undated, row).FullAmountUsd);
        Assert.Equal(ApiPriceValuationState.Partial, Quote(undated, row, ApiPriceTimeMode.DateEffective).State);
        var dated = Snapshot(ApiPriceOrigin.OfficialCurated, Rate(2, from: At.AddDays(-20), until: At.AddDays(-5)), Rate(4, from: At.AddDays(-5)));
        Assert.Equal(2, Quote(dated, row, ApiPriceTimeMode.DateEffective).FullAmountUsd);
        Assert.Equal(4, Quote(dated, row with { VerifiedEventAtUtc = At.AddDays(-5) }, ApiPriceTimeMode.DateEffective).FullAmountUsd);
        Assert.Equal(4, Quote(dated, row).FullAmountUsd);
        Assert.Equal(ApiPriceValuationState.Partial, Quote(dated, row with { VerifiedEventAtUtc = null }, ApiPriceTimeMode.DateEffective).State);
    }

    [Fact]
    public void ProductTierContextAndCacheWritePolicyNeverBorrowAnotherCoordinate()
    {
        var snapshot = Snapshot(ApiPriceOrigin.OfficialCurated, Rate(2), Rate(2.5m, TtTokenCategory.CacheWrite));
        foreach (var context in new[] { Context with { Product = "other" }, Context with { Tier = "fast" }, Context with { ContextBand = "long" } })
            Assert.Equal(ApiPriceValuationState.Partial, Quote(snapshot, Work(), context: context).State);
        Assert.Equal(2, Quote(snapshot, Work(), context: Context with { CacheWritePolicy = null }).FullAmountUsd);
        var write = new ApiPriceWorkloadItem(Coordinate.Model, 0, 0, 1_000_000, 0, 0, 1_000_000);
        Assert.Equal(2.5m, Quote(snapshot, write).FullAmountUsd);
        Assert.Equal(ApiPriceValuationState.Partial, Quote(snapshot, write, context: Context with { CacheWritePolicy = "12h" }).State);
        Assert.Equal(ApiPriceValuationState.Partial, Quote(snapshot, write, context: Context with { CacheWritePolicy = null }).State);
    }

    [Fact]
    public void OverridesAndOptInFallbacksStayLabelledAndMissingPricesRetainCoveredSubtotals()
    {
        var primary = Snapshot(); var local = Snapshot(ApiPriceOrigin.LocalOverride, Rate(0));
        var community = Snapshot(ApiPriceOrigin.CommunityFallback, Rate(9, TtTokenCategory.NonReasoningOutput));
        var row = Work(output: 1_000_000);
        var partial = Quote(primary, row, overrides: local, fallback: community);
        Assert.Equal(ApiPriceValuationState.Partial, partial.State); Assert.Null(partial.FullAmountUsd);
        Assert.Equal(0, partial.CoveredAmountUsd); Assert.Equal(1_000_000, partial.UnpricedTokens);
        Assert.Equal(ApiPriceOrigin.LocalOverride, Assert.Single(partial.Contributions).Origin);
        var complete = Quote(primary, row, overrides: local, fallback: community, allowFallback: true);
        Assert.Equal(9, complete.FullAmountUsd); Assert.Equal(2, complete.Contributions.Count);
        Assert.Contains(complete.Contributions, x => x.Origin == ApiPriceOrigin.CommunityFallback);
        Assert.Throws<ArgumentException>(() => Quote(community, row));
        Assert.Equal(2, Quote(primary, Work(), overrides: Snapshot(ApiPriceOrigin.LocalOverride, Rate(null))).FullAmountUsd);
        Assert.Equal(ApiPriceValuationState.Partial, Quote(primary, Work() with { Model = "unlisted" }).State);
    }

    [Fact]
    public void OldAndFutureSourceClocksRemainVisibleWithoutChangingPricesOrTTBasis()
    {
        var primary = Snapshot(); var old = Quote(primary, Work(), at: At.AddDays(31));
        Assert.True(old.UsesStaleSource); Assert.Equal(2, old.FullAmountUsd);
        Assert.True(Quote(primary, Work(), at: At.AddDays(-1)).HasFutureSourceTime);
        var unknownClock = new ApiPriceCatalogSnapshot(primary.Contract, ApiPriceOrigin.LegacyPinnedFallback, "legacy time unknown", null,
            primary.Currency, primary.RateUnit, primary.SourceUris, primary.Rates, Context);
        Assert.True(Quote(unknownClock, Work(), allowFallback: true).HasUnknownSourceTime);
        var basis = TtJointWorkloadBasisTests.Basis(); string id = basis.BasisId; decimal? score = basis.ScoreExact(TtJointWorkloadBasisTests.Workload());
        _ = Quote(Snapshot(ApiPriceOrigin.OfficialCurated, Rate(200)), Work());
        Assert.Equal(id, basis.BasisId); Assert.Equal(score, basis.ScoreExact(TtJointWorkloadBasisTests.Workload()));
    }

    [Fact]
    public void InconsistentQuantitiesAndArithmeticLimitNeverBecomePlausibleZeroQuotes()
    {
        Assert.Equal(ApiPriceValuationState.Inconsistent, Quote(Snapshot(), Work() with { ReportedTotal = 1 }).State);
        Assert.Equal(ApiPriceValuationState.Inconsistent, Quote(Snapshot(), Work() with { UncachedInput = -1 }).State);
        var huge = Quote(Snapshot(ApiPriceOrigin.OfficialCurated, Rate(decimal.MaxValue)), Work(decimal.MaxValue));
        Assert.Equal(ApiPriceValuationState.ArithmeticUnavailable, huge.State); Assert.Null(huge.CoveredAmountUsd);
        Assert.Equal(0, Quote(Snapshot(), Work(0)).FullAmountUsd);
    }
}
