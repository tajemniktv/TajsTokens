using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

/// <summary>
/// User-invoked local execution boundary for a custom Codex CLI. It is intentionally separate
/// from telemetry acquisition and does not imply that command output is durable evidence.
/// </summary>
public interface ICodexCliHarness
{
    Task<CodexCliRunResult> RunAsync(
        CodexCliHarnessRequest request,
        IProgress<CodexCliOutputLine>? output,
        CancellationToken cancellationToken);
}
