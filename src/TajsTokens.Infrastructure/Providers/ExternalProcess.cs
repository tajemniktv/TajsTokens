using System.Diagnostics;
using System.Text;

namespace TajsTokens.Infrastructure.Providers;

internal sealed record ExternalCommandResult(int ExitCode, string StandardOutput, string StandardError);

internal static class ExternalProcess
{
    public static ProcessStartInfo CreateStartInfo(string command, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows() && !Path.IsPathFullyQualified(command))
        {
            // Using cmd.exe lets PATH/PATHEXT resolve both native executables and npm/bun .cmd shims.
            // Keep this only for command names. Concrete executable paths are launched directly below
            // so cmd.exe cannot expand shell syntax such as %VAR% inside an otherwise valid path.
            startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(BuildWindowsCommandLine(command, arguments));
        }
        else
        {
            startInfo = new ProcessStartInfo(command);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        return startInfo;
    }

    public static async Task<ExternalCommandResult> RunToCompletionAsync(
        string command,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        using var process = new Process();
        process.StartInfo = CreateStartInfo(command, arguments);
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start {command}.");
            }

            // One-shot CLI invocations never write to stdin. Close it immediately so commands that
            // probe/read stdin observe EOF instead of waiting until our timeout. Long-lived protocol
            // clients (such as Codex app-server) use CreateStartInfo directly and keep stdin open.
            process.StandardInput.Close();

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            return new ExternalCommandResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"{command} did not respond within {timeout.TotalSeconds:0} seconds.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup only. The original provider error is more useful to the caller.
        }
    }

    internal static string BuildWindowsCommandLine(string command, IReadOnlyList<string> arguments)
    {
        var parts = new List<string>(arguments.Count + 1) { QuoteForCmd(command) };
        parts.AddRange(arguments.Select(QuoteForCmd));
        return string.Join(' ', parts);
    }

    private static string QuoteForCmd(string value)
    {
        // Commas and @ are ordinary characters to cmd.exe and are common in CLI enums/package names
        // such as Tokscale's client,model group-by value and tokscale@latest. Quoting those values is
        // unnecessary and, through nested cmd -> npm .cmd shims, can preserve the quote characters as
        // part of argv. Keep shell metacharacters (&|<>^%!()) on the quoted path instead.
        if (value.Length > 0 && value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':' or '/' or '\\' or ',' or '@' or '+' or '='))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
