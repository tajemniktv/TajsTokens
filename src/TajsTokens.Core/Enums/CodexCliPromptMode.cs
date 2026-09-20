// Taj's Tokens | CodexCliPromptMode.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Enums;

/// <summary>
///     Describes how the harness supplies the prompt to the configured Codex CLI.
/// </summary>
public enum CodexCliPromptMode
{
    StandardInput,
    LastArgument,
    None,
}