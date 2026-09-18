using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Providers;

/// <summary>Separate bounded read session. Codex owns authentication; no auth files or web routes.</summary>
public sealed class CodexAppServerEvidenceProvider : ICodexServerEvidenceProvider
{
    public const int MaxThreads = 6;
    public async Task<CodexServerCollection> CollectAsync(IReadOnlyList<string> threadIds, CancellationToken cancellationToken)
    {
        if (threadIds.Count > MaxThreads || threadIds.Any(x => !Guid.TryParse(x, out _)))
            throw new ArgumentException("At most six native UUID thread IDs may be requested.", nameof(threadIds));
        var started = DateTimeOffset.UtcNow;
        var observations = new List<CodexServerObservation>();
        string? version = null;
        string? before = null;
        string? after = null;
        DateTimeOffset? beforeCollected = null;
        DateTimeOffset? afterCollected = null;
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(TimeSpan.FromSeconds(100));
        using var process = new Process { StartInfo = ExternalProcess.CreateStartInfo(
            CodexAppServerQuotaProvider.ResolveCodexCommand(), ["app-server", "--listen", "stdio://"]) };
        Task? stderr = null;
        try
        {
            if (!process.Start()) throw new IOException("App-server did not start.");
            stderr = DrainAsync(process.StandardError, total.Token);
            var id = 0;
            async Task<string> Request(string method, object? parameters = null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var requestId = id++;
                var message = new Dictionary<string, object?> { ["method"] = method, ["id"] = requestId };
                if (parameters is not null) message["params"] = parameters;
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), timeout.Token);
                await process.StandardInput.FlushAsync(timeout.Token);
                for (var lines = 0; lines < 1000; lines++)
                {
                    var line = await ReadBoundedLineAsync(process.StandardOutput, timeout.Token);
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("id", out var responseId) && responseId.TryGetInt32(out var n) && n == requestId)
                        return line;
                }
                throw new IOException("App-server notification bound exceeded.");
            }

            var initialized = await Request("initialize", new { clientInfo = new { name = "tajs-tokens", title = "TajsTokens", version = "0.1" } });
            using (var doc = JsonDocument.Parse(initialized))
            {
                var result = doc.RootElement.GetProperty("result");
                if (result.TryGetProperty("userAgent", out var agent))
                {
                    var match = Regex.Match(agent.GetString() ?? "", @"\b\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    if (match.Success) version = match.Value;
                }
            }
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
            await process.StandardInput.FlushAsync(total.Token);
            // A usage response has no native account field. Bracket it with rate-limit identity,
            // and retain this as correlation. Failure or account change cannot bind the payload.
            before = ReadAccount(await Request("account/rateLimits/read"));
            beforeCollected = DateTimeOffset.UtcNow;
            var activityStarted = DateTimeOffset.UtcNow;
            observations.Add(CodexServerEvidenceParser.ParseUsage(await Request("account/usage/read"), null,
                activityStarted, DateTimeOffset.UtcNow, version));
            foreach (var thread in threadIds.Distinct(StringComparer.Ordinal))
            {
                var threadStarted = DateTimeOffset.UtcNow;
                observations.Add(CodexServerEvidenceParser.ParseUsage(await Request("account/usage/read", new { threadId = thread }),
                    thread, threadStarted, DateTimeOffset.UtcNow, version));
            }
            after = ReadAccount(await Request("account/rateLimits/read"));
            afterCollected = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Bounded fixed diagnostics; raw stderr/errors may contain credential/account material.
            var detail = e is OperationCanceledException ? "App-server evidence read timed out; no credentials retained." :
                "App-server evidence session failed; no raw error or credentials retained.";
            if (!observations.Any(x => x.Surface == CodexServerSurface.AccountActivity))
                observations.Add(CodexServerEvidenceParser.New(CodexServerSurface.AccountActivity, null, started, DateTimeOffset.UtcNow, version)
                    with { State = ServerEvidenceState.Error, Detail = detail });
            foreach (var thread in threadIds.Distinct().Where(t => !observations.Any(x => x.ThreadId == t)))
                observations.Add(CodexServerEvidenceParser.New(CodexServerSurface.ThreadUsage, thread, started, DateTimeOffset.UtcNow, version)
                    with { State = ServerEvidenceState.Error, Detail = detail });
        }
        finally
        {
            ExternalProcess.TryKill(process);
            total.Cancel();
            if (stderr is not null) { try { await stderr; } catch (OperationCanceledException) { } catch (IOException) { } }
        }
        observations = observations.Select(x => Correlate(x, before, after) with
            { AccountBracket = new(before, beforeCollected, after, afterCollected) }).ToList();
        foreach (var surface in new[] { CodexServerSurface.PlanHistory, CodexServerSurface.GroupedAnalytics })
            observations.Add(CodexServerEvidenceParser.New(surface, null, started, DateTimeOffset.UtcNow, version) with
            {
                State = ServerEvidenceState.NoSupportedSeam,
                Detail = "No method in installed/upstream app-server contract. TUI backend authentication remains owned by Codex; no direct request attempted."
            });
        return new(observations);
    }

    public static CodexServerObservation Correlate(CodexServerObservation observation, string? before, string? after) =>
        before is not null && after is not null && before != after
            ? observation with { AccountEvidence = AccountEvidenceClass.Conflicting, State = ServerEvidenceState.Conflict,
                Detail = "Account changed across the request batch; payload retained without account attribution." }
            : before is not null && before == after
                ? observation with { AccountEvidence = AccountEvidenceClass.ServerCorrelated, CorrelatedAccountKey = before }
                : observation with { AccountEvidence = AccountEvidenceClass.Unattributed, CorrelatedAccountKey = null };

    private static string? ReadAccount(string response)
    {
        try { return CodexAppServerQuotaProvider.ParseQuotaResponse(response, DateTimeOffset.UtcNow).AccountKey; }
        catch (Exception e) when (e is InvalidOperationException or JsonException) { return null; }
    }
    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0) { }
    }
    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        // ReadLineAsync allocates without a bound. Limit each JSON-RPC message to 2 MiB.
        var line = new System.Text.StringBuilder();
        var buffer = new char[1];
        while (line.Length < 2 * 1024 * 1024)
        {
            if (await reader.ReadAsync(buffer.AsMemory(), token) == 0) throw new IOException("App-server closed output.");
            if (buffer[0] == '\n') return line.ToString();
            line.Append(buffer[0]);
        }
        throw new IOException("App-server response exceeds evidence bound.");
    }
}
