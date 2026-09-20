// Taj's Tokens | CodexSession.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public sealed record CodexSession(
    string SessionId,
    string? ThreadId,
    string Repository,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? LastActivityAtUtc,
    string Status);