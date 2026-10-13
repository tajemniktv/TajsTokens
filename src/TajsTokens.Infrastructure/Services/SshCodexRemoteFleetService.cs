// Taj's Tokens | SshCodexRemoteFleetService.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using System.Diagnostics;
using System.Text.Json;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Services;

/// <summary>
///     Runs a content-free, read-only probe through an already configured SSH alias. Nothing is
///     installed or synchronized; quota and account observations are intentionally not merged.
/// </summary>
public sealed class SshCodexRemoteFleetService
{
    private const string Probe = """
        import json, os, pathlib, sqlite3, time

        home = pathlib.Path(os.environ.get('CODEX_HOME') or pathlib.Path.home() / '.codex')
        rows = {}
        errors = 0
        history_available = False
        bounded = False
        def seconds(value):
            if not isinstance(value, int):
                return 0
            return value // 1000 if value > 100000000000 else value
        for path in sorted(home.glob('thread_history_*.sqlite'), reverse=True):
            try:
                db = sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True, timeout=2)
                try:
                    columns = {r[1] for r in db.execute('PRAGMA table_info(thread_turns)')}
                    if not {'thread_id', 'turn_id', 'status'} <= columns:
                        continue
                    history_available = True
                    order = 'rollout_ordinal DESC' if 'rollout_ordinal' in columns else 'rowid DESC'
                    started = 'started_at' if 'started_at' in columns else 'NULL AS started_at'
                    completed = 'completed_at' if 'completed_at' in columns else 'NULL AS completed_at'
                    sql = ('SELECT thread_id, turn_id, status, started_at, completed_at FROM '
                           '(SELECT thread_id, turn_id, status, ' + started + ', ' + completed + ', '
                           'ROW_NUMBER() OVER (PARTITION BY thread_id ORDER BY ' + order + ') rn '
                           'FROM thread_turns) WHERE rn=1 LIMIT 10001')
                    for index, (thread_id, turn_id, status, start, end) in enumerate(db.execute(sql)):
                        if index >= 10000:
                            bounded = True
                            break
                        if not isinstance(thread_id, str) or not isinstance(status, str):
                            continue
                        observed = end if isinstance(end, int) else start if isinstance(start, int) else None
                        previous = rows.get(thread_id)
                        if previous is None or seconds(observed) > seconds(previous['observed']):
                            rows[thread_id] = {'id': thread_id, 'status': status,
                                               'started': start if isinstance(start, int) else None,
                                               'observed': observed}
                finally:
                    db.close()
            except (OSError, sqlite3.Error):
                errors += 1

        now = int(time.time())
        visible = [r for r in rows.values() if r['status'] == 'inProgress' or
                   (r['observed'] is not None and seconds(r['observed']) >= now - 86400)]
        visible.sort(key=lambda r: (r['status'] != 'inProgress', -seconds(r['observed'])))
        print(json.dumps({'v': 1, 'captured': now, 'threads': visible[:150],
                          'truncated': bounded or len(visible) > 150, 'source_errors': errors,
                          'history_available': history_available}, separators=(',', ':')))
        """;

    public async Task<CodexRemoteFleetHostResult> ReadHostAsync(string host, CancellationToken cancellationToken)
    {
        if (!CodexRemoteFleetHostPolicy.IsValid(host))
            throw new ArgumentException("Use a simple SSH config host alias without options or shell characters.", nameof(host));

        (string? output, string? error) = await RunProbeAsync(host, cancellationToken);
        if (output is not null)
        {
            try { return Parse(host, output); }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentOutOfRangeException)
            {
                error = "Remote probe returned invalid metadata.";
            }
        }
        return new CodexRemoteFleetHostResult(host, DateTimeOffset.UtcNow, [], error ?? "Remote probe unavailable.", false);
    }

    private static async Task<(string? Output, string? Error)> RunProbeAsync(
        string host, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var start = new ProcessStartInfo("ssh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[]
                 { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=5", "-T", host, "python3", "-" })
            start.ArgumentList.Add(argument);
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("SSH did not start.");
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.StandardInput.WriteAsync(Probe.AsMemory(), timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                string output = await outputTask;
                await errorTask; // Never surface remote stderr, which can contain user paths or content.
                return process.ExitCode == 0 && output.Length is > 0 and <= 131072
                    ? (output, null)
                    : (null, $"SSH probe failed (exit {process.ExitCode}). Check host access and Python availability.");
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "SSH probe timed out.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (null, "SSH could not start or complete on this computer.");
        }
    }

    internal static CodexRemoteFleetHostResult Parse(string host, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.GetProperty("v").GetInt32() != 1) throw new JsonException("Unsupported remote probe version.");
        DateTimeOffset captured = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("captured").GetInt64());
        var threads = new List<CodexRemoteFleetThread>();
        foreach (JsonElement row in root.GetProperty("threads").EnumerateArray().Take(150))
        {
            string? id = row.GetProperty("id").GetString();
            string? status = row.GetProperty("status").GetString();
            if (!CodexRemoteFleetHostPolicy.IsSafeThreadId(id)) continue;
            // Remote SQLite fields are untrusted input. Do not display or retain arbitrary text.
            status = CodexRemoteFleetHostPolicy.NormalizeStatus(status);
            threads.Add(new CodexRemoteFleetThread(host, id, status,
                ReadUnixTime(row, "started"), ReadUnixTime(row, "observed")));
        }
        if (threads.Select(thread => thread.ThreadId).Distinct(StringComparer.Ordinal).Count() != threads.Count)
            throw new JsonException("Duplicate remote thread identity.");
        int sourceErrors = root.GetProperty("source_errors").GetInt32();
        return new CodexRemoteFleetHostResult(host, captured, threads, null,
            root.GetProperty("truncated").GetBoolean() || root.GetProperty("threads").GetArrayLength() > 150,
            Math.Clamp(sourceErrors, 0, 10_000),
            root.GetProperty("history_available").GetBoolean()) { CollectedAtUtc = DateTimeOffset.UtcNow };
    }

    private static DateTimeOffset? ReadUnixTime(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long seconds)) return null;
        try
        {
            return Math.Abs(seconds) >= 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(seconds)
                : DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
