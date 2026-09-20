// Taj's Tokens | UsageEvent.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public sealed record UsageEvent(
    string EventId,
    string SessionId,
    DateTimeOffset TimestampUtc,
    string EventType,
    string Summary,
    double? TokenDelta);