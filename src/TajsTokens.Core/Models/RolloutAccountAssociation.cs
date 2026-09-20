// Taj's Tokens | RolloutAccountAssociation.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public enum QuotaAccountAttribution
{
    Unattributed,
    ProviderVerified,
    UserAsserted,
}

/// <summary>User-owned association, never a replacement for a source's AccountKey.</summary>
public sealed record RolloutAccountAssociation(
    string Id,
    string Provider,
    string Profile,
    string SourceIdentity,
    string SessionId,
    DateTimeOffset FromUtc,
    DateTimeOffset ThroughUtc,
    string AccountKey,
    DateTimeOffset AssertedAtUtc)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Id) && Provider == "codex" &&
                           !string.IsNullOrWhiteSpace(Profile) && !string.IsNullOrWhiteSpace(SourceIdentity) &&
                           !string.IsNullOrWhiteSpace(SessionId) && !string.IsNullOrWhiteSpace(AccountKey) &&
                           FromUtc <= ThroughUtc && ThroughUtc <= AssertedAtUtc;
}