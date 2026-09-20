// Taj's Tokens | ResetEvent.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;

#endregion

namespace TajsTokens.Core.Models;

public sealed record ResetEvent(
    string EventId,
    QuotaWindowKind Kind,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset EffectiveAtUtc,
    string Source);