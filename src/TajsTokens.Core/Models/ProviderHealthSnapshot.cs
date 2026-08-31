namespace TajsTokens.Core.Models;

public enum ProviderHealthState
{
    Healthy,
    Refreshing,
    Stale,
    Unavailable,
    AuthenticationError,
    Error
}

public sealed record ProviderHealthSnapshot(
    string Provider,
    ProviderHealthState State,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset? LastSuccessUtc,
    string? Detail = null);
