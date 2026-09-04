namespace TajsTokens.Core.Enums;

/// <summary>
/// Describes how the harness supplies the prompt to the configured Codex CLI.
/// </summary>
public enum CodexCliPromptMode
{
    StandardInput,
    LastArgument,
    None
}
