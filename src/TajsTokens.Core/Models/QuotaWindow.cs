// Taj's Tokens | QuotaWindow.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;

#endregion

namespace TajsTokens.Core.Models;

public sealed record QuotaWindow(
    QuotaWindowKind Kind,
    DateTimeOffset? WindowStartUtc,
    DateTimeOffset? WindowEndUtc,
    int? WindowMinutes,
    string Provider,
    string Profile)
{
    public TimeSpan? Duration => WindowStartUtc is not null && WindowEndUtc is not null
        ? WindowEndUtc - WindowStartUtc
        : WindowMinutes is not null
            ? TimeSpan.FromMinutes(WindowMinutes.Value)
            : null;
}