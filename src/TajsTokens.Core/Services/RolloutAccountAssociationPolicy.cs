using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public static class RolloutAccountAssociationPolicy
{
    public static IReadOnlyList<RolloutAccountAssociation> Matches(string provider, string profile,
        string? sourceIdentity, string? sessionId, DateTimeOffset observedAt,
        IReadOnlyList<RolloutAccountAssociation> associations, DateTimeOffset asOf) =>
        associations.Where(x => x.IsValid && x.AssertedAtUtc <= asOf && x.Provider == provider &&
            x.Profile == profile && x.SourceIdentity == sourceIdentity && x.SessionId == sessionId &&
            observedAt >= x.FromUtc && observedAt <= x.ThroughUtc).ToArray();

    public static RolloutAccountAssociation? Resolve(QuotaSnapshot row,
        IReadOnlyList<RolloutAccountAssociation> associations, DateTimeOffset asOf)
    {
        if (row.AccountKey is not null || row.Authority != QuotaObservationAuthority.EmbeddedObservation) return null;
        var matches = Matches(row.Provider, row.Profile, row.SourceIdentity, row.SessionId,
            row.CapturedAtUtc, associations, asOf);
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
