// Taj's Tokens | QuotaAccountScope.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

/// <summary>Presentation of a source-qualified backend-account pseudonym, never a person or workload identity.</summary>
public static class QuotaAccountScope
{
    public static string Describe(string? key)
    {
        return string.IsNullOrEmpty(key)
            ? "Backend account unknown"
            : $"Backend account …{key[Math.Max(0, key.Length - 8)..]}";
    }
}