using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

/// <summary>
/// A single user-requested local invocation of a custom Codex CLI.
/// </summary>
public sealed record CodexCliHarnessRequest(
    string Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    string Prompt,
    CodexCliPromptMode PromptMode,
    TimeSpan Timeout)
{
    public static CodexCliHarnessRequest FromSettings(RuntimeSettings settings, string prompt) =>
        new(
            settings.CodexCliCommand,
            CodexCliArguments.ParseLines(settings.CodexCliArguments),
            settings.CodexCliWorkingDirectory,
            prompt,
            settings.CodexCliPromptMode,
            TimeSpan.FromSeconds(settings.CodexCliTimeoutSeconds));
}

public sealed record CodexCliOutputLine(
    CodexCliOutputStream Stream,
    string Text,
    DateTimeOffset CapturedAtUtc);

public enum CodexCliOutputStream
{
    StandardOutput,
    StandardError
}

public sealed record CodexCliRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc)
{
    public bool Succeeded => ExitCode == 0;
    public TimeSpan Duration => FinishedAtUtc - StartedAtUtc;
}

/// <summary>
/// The harness stores one argument per line. This avoids shell parsing and keeps each argument an
/// explicit value when it crosses the process boundary.
/// </summary>
public static class CodexCliArguments
{
    public static IReadOnlyList<string> ParseLines(string? text) =>
        (text ?? string.Empty)
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

    public static string ToLines(IEnumerable<string>? arguments) =>
        string.Join(Environment.NewLine, arguments ?? []);
}
