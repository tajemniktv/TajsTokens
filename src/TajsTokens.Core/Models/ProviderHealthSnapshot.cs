using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public sealed record ProviderHealthSnapshot(
    string Provider,
    TelemetryHealthState State,
    string Detail,
    DateTimeOffset? LastSuccessUtc = null);
