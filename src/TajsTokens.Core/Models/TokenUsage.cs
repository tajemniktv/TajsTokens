// Taj's Tokens | TokenUsage.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public sealed record TokenUsage(
    string Provider,
    string Client,
    string Model,
    DateTimeOffset ObservedAtUtc,
    TokenBreakdown Breakdown,
    string? Profile = null,
    string? SessionId = null,
    string? ThreadId = null,
    string? Repository = null,
    string? AgentId = null);