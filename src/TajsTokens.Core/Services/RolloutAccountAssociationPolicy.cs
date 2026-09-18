using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public static class RolloutAccountAssociationPolicy
{
    public static RolloutAccountAssociation? Resolve(QuotaSnapshot row,
        IReadOnlyList<RolloutAccountAssociation> associations, DateTimeOffset asOf)
    {
        if (row.AccountKey is not null || row.Authority != QuotaObservationAuthority.EmbeddedObservation) return null;
        var matches = associations.Where(x => x.IsValid && x.AssertedAtUtc <= asOf && x.Provider == row.Provider &&
            x.Profile == row.Profile && x.SourceIdentity == row.SourceIdentity && x.SessionId == row.SessionId &&
            row.CapturedAtUtc >= x.FromUtc && row.CapturedAtUtc <= x.ThroughUtc).ToArray();
        // Ambiguous overlapping ownership is unusable, not last-writer-wins attribution.
        return matches.Select(x => x.AccountKey).Distinct().Count() == 1
            ? matches.OrderBy(x => x.AssertedAtUtc).First() : null;
    }

    public static IReadOnlyList<RolloutAccountAssociation> Create(CodexForecastDataset data, string accountKey,
        DateTimeOffset assertedAt)
    {
        if (!data.Quota.Any(x => x.AccountKey == accountKey && x.Authority == QuotaObservationAuthority.ProviderAuthoritative))
            throw new ArgumentException("Select a recorded provider account; an arbitrary account identity cannot be asserted.", nameof(accountKey));
        return data.Quota.Where(x => x.AccountKey is null && x.Authority == QuotaObservationAuthority.EmbeddedObservation &&
                x.SourceIdentity is not null && x.SessionId is not null && x.CapturedAtUtc <= assertedAt)
            .GroupBy(x => (x.Provider, x.Profile, x.SourceIdentity, x.SessionId))
            .Select(g => new RolloutAccountAssociation(Guid.NewGuid().ToString("N"), g.Key.Provider, g.Key.Profile,
                g.Key.SourceIdentity!, g.Key.SessionId!, g.Min(x => x.CapturedAtUtc), g.Max(x => x.CapturedAtUtc), accountKey, assertedAt)).ToArray();
    }
}
