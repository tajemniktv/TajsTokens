using System.Numerics;
using TajsTokens.Core.Models;
using static TajsTokens.Core.Services.ExactTokenWeightArithmetic;

namespace TajsTokens.Core.Services;

/// <summary>Explicit counterfactual/data-effective valuation. No selected source, automatic refresh, billing or TT activation.</summary>
public static class ApiPriceCatalogValuation
{
    public const string Version = "api-price-valuation/v1";
    private static readonly BigInteger Million = Product(1_000_000, 1);
    public static ApiPriceValuation Calculate(ApiPriceCatalogSnapshot primary, IReadOnlyList<ApiPriceWorkloadItem> items,
        ApiPriceReferenceContext context, ApiPriceTimeMode mode, DateTimeOffset atUtc, TimeSpan maximumSourceAge,
        ApiPriceCatalogSnapshot? overrides = null, ApiPriceCatalogSnapshot? fallback = null, bool allowFallback = false)
    {
        ArgumentNullException.ThrowIfNull(primary); ArgumentNullException.ThrowIfNull(items); ArgumentNullException.ThrowIfNull(context);
        if (!Enum.IsDefined(mode) || atUtc == default || maximumSourceAge <= TimeSpan.Zero || items.Count > 500_000 || items.Any(x => x is null) ||
            string.IsNullOrWhiteSpace(context.Product) || string.IsNullOrWhiteSpace(context.Tier) || string.IsNullOrWhiteSpace(context.ContextBand) ||
            overrides is not null && overrides.Origin != ApiPriceOrigin.LocalOverride ||
            fallback is not null && fallback.Origin is not (ApiPriceOrigin.CommunityFallback or ApiPriceOrigin.LegacyPinnedFallback) ||
            !allowFallback && primary.Origin is ApiPriceOrigin.CommunityFallback or ApiPriceOrigin.LegacyPinnedFallback)
            throw new ArgumentException("Unsupported valuation context or source roles.");
        ApiPriceValuation Failure(ApiPriceValuationState state, int inconsistent = 0) => new(Version, mode, context, atUtc, state, null, null, null, null, inconsistent, false, false, false, []);
        try
        {
            var contributions = new Dictionary<string, (ApiPriceCatalogSnapshot Snapshot, BigInteger Amount, decimal Tokens)>();
            decimal priced = 0, unpriced = 0; int invalid = 0;
            foreach (var item in items)
            {
                decimal[] amounts = [item.UncachedInput, item.CacheRead, item.CacheWrite, item.NonReasoningOutput, item.ReasoningOutput];
                if (item.SourceInconsistent || item.ReportedTotal < 0 || amounts.Any(x => x < 0) || ExactSum(amounts) != item.ReportedTotal) { invalid++; continue; }
                for (int i = 0; i < amounts.Length; i++)
                {
                    decimal tokens = amounts[i]; if (tokens == 0) continue;
                    ApiPriceCatalogSnapshot? chosen = null; decimal? price = null;
                    if (!string.IsNullOrWhiteSpace(item.Model))
                    {
                        var coordinate = new ApiPriceCoordinate(item.Model, context.Product, context.Tier, context.ContextBand);
                        foreach (var snapshot in new[] { overrides, primary, allowFallback ? fallback : null })
                        {
                            var rate = snapshot?.Find(coordinate, (TtTokenCategory)i, context.CacheWritePolicy, mode, item.VerifiedEventAtUtc, atUtc);
                            if (rate?.UsdPerMillion is not { } value) continue;
                            chosen = snapshot; price = value; break;
                        }
                    }
                    if (chosen is null || price is null) { unpriced = ExactSum([unpriced, tokens]); continue; }
                    priced = ExactSum([priced, tokens]);
                    var old = contributions.GetValueOrDefault(chosen.SnapshotId, (chosen, BigInteger.Zero, 0m));
                    contributions[chosen.SnapshotId] = (chosen, old.Item2 + Product(tokens, price.Value), ExactSum([old.Item3, tokens]));
                }
            }
            if (invalid > 0) return Failure(ApiPriceValuationState.Inconsistent, invalid);
            BigInteger total = contributions.Values.Aggregate(BigInteger.Zero, (sum, c) => sum + c.Amount);
            decimal amount = Ratio(total, Million, 0);
            var parts = contributions.Values.OrderBy(x => x.Snapshot.SnapshotId, StringComparer.Ordinal)
                .Select(x => new ApiPriceContribution(x.Snapshot.SnapshotId, x.Snapshot.Origin, x.Snapshot.SourceName, Ratio(x.Amount, Million, 0), x.Tokens)).ToArray();
            return new(Version, mode, context, atUtc, unpriced == 0 ? ApiPriceValuationState.Complete : ApiPriceValuationState.Partial,
                unpriced == 0 ? amount : null, amount, priced, unpriced, 0,
                contributions.Values.Any(x => x.Snapshot.RetrievedAtUtc is { } retrieved && atUtc - retrieved > maximumSourceAge),
                contributions.Values.Any(x => x.Snapshot.RetrievedAtUtc > atUtc),
                contributions.Values.Any(x => x.Snapshot.RetrievedAtUtc is null), Array.AsReadOnly(parts));
        }
        catch (OverflowException) { return Failure(ApiPriceValuationState.ArithmeticUnavailable); }
    }
    public static ApiPriceWorkloadItem FromJoint(CodexJointWorkloadCell cell) => new(cell.Coordinate?.Model, cell.UncachedInput,
        cell.CacheRead, cell.CacheWrite, cell.NonReasoningOutput, cell.ReasoningOutput, cell.ReportedTotal, SourceInconsistent: !cell.IsComplete);
}
