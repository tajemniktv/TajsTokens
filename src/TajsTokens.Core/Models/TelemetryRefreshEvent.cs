// Taj's Tokens | TelemetryRefreshEvent.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public sealed record TelemetryRefreshEvent(
    DateTimeOffset TimestampUtc,
    string Type,
    string Description);