// Taj's Tokens | CodexRolloutReadException.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Ingestion;

internal sealed class CodexRolloutReadException(CodexRolloutReadFailure failure)
    : IOException($"Rollout read failed: {failure}.")
{
    public CodexRolloutReadFailure Failure { get; } = failure;
}
