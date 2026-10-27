// Taj's Tokens | CodexSourceTimestampQuality.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

/// <summary>How much source evidence supports an event timestamp.</summary>
public enum CodexSourceTimestampQuality
{
    Valid,
    Missing,
    Malformed,
    LegacyUnverified,
    Mixed,
}
