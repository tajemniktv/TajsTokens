// Taj's Tokens | ProviderHealthSnapshot.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;

#endregion

namespace TajsTokens.Core.Models;

public sealed record ProviderHealthSnapshot(
    string Provider,
    TelemetryHealthState State,
    string Detail,
    DateTimeOffset? LastSuccessUtc = null);