// Taj's Tokens | Agent.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;

#endregion

namespace TajsTokens.Core.Models;

public sealed record Agent(
    string AgentId,
    string SessionId,
    string Name,
    AgentRuntimeState State,
    DateTimeOffset LastSeenUtc,
    string? Model);