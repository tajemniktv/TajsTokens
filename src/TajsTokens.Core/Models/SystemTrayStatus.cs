// Taj's Tokens | SystemTrayStatus.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public sealed record SystemTrayStatus(
    string Tooltip,
    int? ConstrainedRemainingPercent,
    bool IsFresh,
    string HealthLabel);