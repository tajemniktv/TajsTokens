// Taj's Tokens | QuotaSourceAccountInventory.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

/// <summary>Observed quota-source identities only; this does not attribute local work or prove a person.</summary>
public sealed record QuotaSourceAccountObservation(
    string Provider,
    string Profile,
    string Source,
    string? AccountKey,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastObservedAtUtc,
    long ObservationCount);

public sealed record QuotaSourceAccountInventory(
    IReadOnlyList<QuotaSourceAccountObservation> Observations,
    bool Truncated);
