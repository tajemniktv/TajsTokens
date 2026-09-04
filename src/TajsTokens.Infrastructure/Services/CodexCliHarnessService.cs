using System.Diagnostics;
using System.Text;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Providers;

namespace TajsTokens.Infrastructure.Services;

/// <summary>
/// Runs a configured custom Codex CLI on explicit user request. The process boundary is kept
/// separate from Codex telemetry collection; prompts and output are returned to the caller only.
/// </summary>
public sealed class CodexCliHarnessService : ICodexCliHarness
{
    public async Task<CodexCliRunResult> RunAsync(
        CodexCliHarnessRequest request,
        IProgress<CodexCliOutputLine>? output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var arguments = BuildArguments(request);
        using var process = new Process
        {
            StartInfo = ExternalProcess.CreateStartInfo(request.Command, arguments)
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            process.StartInfo.WorkingDirectory = Path.GetFullPath(request.WorkingDirectory);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(request.Timeout);
        var processToken = timeoutSource.Token;
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stdoutTask = Task.CompletedTask;
        var stderrTask = Task.CompletedTask;

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start the configured Codex CLI '{request.Command}'.");
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            stdoutTask = ReadStreamAsync(
                process.StandardOutput,
                CodexCliOutputStream.StandardOutput,
                stdout,
                output,
                CancellationToken.None);
            stderrTask = ReadStreamAsync(
                process.StandardError,
                CodexCliOutputStream.StandardError,
                stderr,
                output,
                CancellationToken.None);

            if (request.PromptMode == CodexCliPromptMode.StandardInput && request.Prompt.Length > 0)
            {
                await process.StandardInput.WriteAsync(request.Prompt.AsMemory(), processToken);
                await process.StandardInput.FlushAsync(processToken);
            }

            // Closing stdin is important for CLIs that use stdin as their prompt transport: it
            // marks the request complete and prevents a waiting custom CLI from hanging forever.
            process.StandardInput.Close();
            await process.WaitForExitAsync(processToken);
            await Task.WhenAll(stdoutTask, stderrTask);

            return new CodexCliRunResult(
                process.ExitCode,
                stdout.ToString(),
                stderr.ToString(),
                startedAtUtc,
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ExternalProcess.TryKill(process);
            await IgnoreStreamCompletionAsync(stdoutTask, stderrTask);
            throw new TimeoutException(
                $"The configured Codex CLI did not finish within {request.Timeout.TotalSeconds:0} seconds.");
        }
        catch
        {
            ExternalProcess.TryKill(process);
            await IgnoreStreamCompletionAsync(stdoutTask, stderrTask);
            throw;
        }
    }

    internal static IReadOnlyList<string> BuildArguments(CodexCliHarnessRequest request)
    {
        var arguments = request.Arguments.ToList();
        if (request.PromptMode == CodexCliPromptMode.LastArgument && request.Prompt.Length > 0)
        {
            arguments.Add(request.Prompt);
        }

        return arguments;
    }

    private static async Task ReadStreamAsync(
        StreamReader reader,
        CodexCliOutputStream stream,
        StringBuilder buffer,
        IProgress<CodexCliOutputLine>? output,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            buffer.AppendLine(line);
            output?.Report(new CodexCliOutputLine(stream, line, DateTimeOffset.UtcNow));
        }
    }

    private static async Task IgnoreStreamCompletionAsync(Task stdoutTask, Task stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch
        {
            // The original process failure or cancellation is the useful result for the caller.
        }
    }

    private static void ValidateRequest(CodexCliHarnessRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            throw new ArgumentException("A Codex CLI executable or PATH command is required.", nameof(request));
        }

        if (Path.IsPathFullyQualified(request.Command) && !File.Exists(request.Command))
        {
            throw new FileNotFoundException("The configured Codex CLI executable was not found.", request.Command);
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory) && !Directory.Exists(request.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"The Codex CLI working directory was not found: {request.WorkingDirectory}");
        }

        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The Codex CLI timeout must be greater than zero.");
        }

        if (request.Arguments.Any(argument => argument is null || argument.Contains('\0')))
        {
            throw new ArgumentException("Codex CLI arguments cannot contain null characters.", nameof(request));
        }
    }
}
