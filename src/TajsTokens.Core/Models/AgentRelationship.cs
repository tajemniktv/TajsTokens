// Taj's Tokens | AgentRelationship.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public sealed record AgentRelationship(
    string ParentAgentId,
    string ChildAgentId,
    DateTimeOffset LinkedAtUtc);