// Taj's Tokens | CodexBackendDailyEvidenceProvider.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Infrastructure.Providers;

/// <summary>Opt-in, fixed-origin, read-only backend adapter. Never refreshes or persists credentials.</summary>
public sealed class CodexBackendDailyEvidenceProvider : ICodexServerEvidenceProvider
{
    private const int MaxBytes = 4 * 1024 * 1024;

    private static readonly HttpClient Client =
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };

    private readonly HttpClient _client;
    private readonly Func<bool> _enabled;
    private readonly Func<CancellationToken, Task<string>> _readAuth;

    public CodexBackendDailyEvidenceProvider(Func<bool> enabled) : this(enabled, Client, ReadAuthAsync)
    {
    }

    internal CodexBackendDailyEvidenceProvider(
        Func<bool> enabled,
        HttpClient client,
        Func<CancellationToken, Task<string>> readAuth)
    {
        _enabled = enabled;
        _client = client;
        _readAuth = readAuth;
    }

    public Task<CodexServerCollection> CollectAsync(IReadOnlyList<string> threadIds, CancellationToken cancellationToken)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        return CollectRangeAsync(today.AddDays(-30), today, cancellationToken);
    }

    private static async Task<string> ReadAuthAsync(CancellationToken token)
    {
        string? home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        string path = Path.Combine(home, "auth.json");
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException();
        return await File.ReadAllTextAsync(path, token);
    }

    /// <summary>Explicit bounded research range; dates are sent verbatim, without assuming endpoint inclusivity.</summary>
    public Task<CodexServerCollection> CollectRangeAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        if (endDate < startDate || endDate.DayNumber - startDate.DayNumber > 30 ||
            endDate > DateOnly.FromDateTime(DateTime.UtcNow))
            throw new ArgumentOutOfRangeException(nameof(endDate), "Range must be ordered, at most 30 days apart, and not future-dated.");
        return CollectCoreAsync(startDate, endDate, null, cancellationToken);
    }

    public Task<CodexServerCollection> CollectHistoricalAsync(IReadOnlyList<string> threadIds, CancellationToken token)
    {
        if (threadIds.Count > 100 || threadIds.Any(x => !Guid.TryParse(x, out _)) || threadIds.Distinct().Count() != threadIds.Count)
            throw new ArgumentException("At most 100 distinct native thread IDs are supported.", nameof(threadIds));
        return CollectCoreAsync(default, default, threadIds, token);
    }

    private async Task<CodexServerCollection> CollectCoreAsync(
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<string>? threads,
        CancellationToken cancellationToken)
    {
        if (!_enabled()) return new CodexServerCollection([]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        CancellationToken token = timeout.Token;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string start = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string end = endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string query = $"?start_date={start}&end_date={end}&group_by=day";
        string[] routes = threads is null
            ? new[] { "wham/analytics/daily-workspace-usage-counts", "wham/usage/daily-token-usage-breakdown" }
            : threads.Count == 0
                ? ["wham/usage/plan_limit_history?days=7"]
                : new[] { "wham/usage/plan_limit_history?days=7", "wham/usage/thread_usage/query_v2" };
        CodexServerObservation[] rows = routes.Select((_, index) => threads is null
            ? New(index == 1, started)
            : new CodexServerObservation(
                Guid.NewGuid().ToString("N"),
                index == 0 ? CodexServerSurface.PlanHistory : CodexServerSurface.TaskUsage,
                null,
                started,
                started,
                CodexServerEvidenceParser.Contract,
                CodexHistoricalAnalyticsParser.Contract,
                ServerEvidenceState.Unavailable,
                "Not collected.")).ToArray();
        string stage = "reading Codex credentials";
        try
        {
            using JsonDocument auth = JsonDocument.Parse(await _readAuth(token));
            JsonElement credentials = auth.RootElement.GetProperty("tokens");
            string? bearer = credentials.GetProperty("access_token").GetString();
            string? account = credentials.GetProperty("account_id").GetString();
            if (string.IsNullOrWhiteSpace(bearer) || string.IsNullOrWhiteSpace(account)) throw new InvalidDataException();
            stage = "verifying initial backend account bracket";
            (string? Account, string? Plan, string Policy, DateTimeOffset At) before = await ReadIdentityAsync(bearer, account, token);
            if (before.Account != account)
                return Failure(rows, ServerEvidenceState.Conflict, "Backend account does not match selected Codex credentials.");
            for (int i = 0; i < routes.Length; i++)
            {
                DateTimeOffset fetch = DateTimeOffset.UtcNow;
                stage = "reading bounded report " + rows[i].Surface;
                try
                {
                    string? body = threads is not null && i == 1
                        ? JsonSerializer.Serialize(
                            new
                            {
                                threads = threads.Select(id =>
                                    new { thread_id = id, created_at = (string?)null, descendant_thread_ids = Array.Empty<string>() }),
                            })
                        : null;
                    string json = await SendAsync(
                        routes[i] + (threads is null ? query + (i == 0 ? "&workspace_user=true" : "") : ""),
                        bearer,
                        account,
                        token,
                        body);
                    if (threads is null)
                    {
                        CodexDailyReport report = CodexDailyReportParser.Parse(json, i == 1, routes[i], start, end);
                        rows[i] = rows[i] with
                        {
                            State = report.Days.Count == 0 ? ServerEvidenceState.Empty : ServerEvidenceState.Available,
                            Detail = "Experimental backend daily snapshot; not live quota or billing-cycle consumption.",
                            DailyReport = report,
                        };
                    }
                    else if (i == 0)
                    {
                        CodexPlanHistoryReport report = CodexHistoricalAnalyticsParser.ParsePlan(json);
                        rows[i] = rows[i] with
                        {
                            State = report.Periods.Count == 0 ? ServerEvidenceState.Empty : ServerEvidenceState.Available,
                            Detail =
                            "Shadow historical allowance report; basis points refer to each historical period. No production calibration.",
                            PlanHistory = report,
                        };
                    }
                    else
                    {
                        CodexTaskUsageReport report = CodexHistoricalAnalyticsParser.ParseTasks(json, threads);
                        rows[i] = rows[i] with
                        {
                            State = report.Threads.Count == 0 ? ServerEvidenceState.Empty : ServerEvidenceState.Available,
                            Detail =
                            "Shadow task report valued against current full allowance, not historical period shares. Descendant coverage is not assumed; task rows are not summed.",
                            TaskUsage = report,
                        };
                    }
                    rows[i] = rows[i] with { FetchStartedAtUtc = fetch, CollectedAtUtc = DateTimeOffset.UtcNow };
                }
                catch (HttpRequestException e)
                {
                    rows[i] = rows[i] with
                    {
                        FetchStartedAtUtc = fetch,
                        CollectedAtUtc = DateTimeOffset.UtcNow,
                        State = e.StatusCode == HttpStatusCode.Unauthorized
                            ? ServerEvidenceState.AuthenticationRequired
                            : ServerEvidenceState.Unavailable,
                        Detail = e.StatusCode is null ? "Backend transport failed." : $"Backend HTTP {(int)e.StatusCode}.",
                    };
                    if (e.StatusCode == HttpStatusCode.Unauthorized)
                        return Failure(
                            rows,
                            ServerEvidenceState.AuthenticationRequired,
                            "Codex sign-in required; no automatic auth refresh attempted.");
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException
                                              or FormatException or OverflowException)
                {
                    rows[i] = rows[i] with
                    {
                        FetchStartedAtUtc = fetch,
                        CollectedAtUtc = DateTimeOffset.UtcNow,
                        State = ServerEvidenceState.Invalid,
                        Detail = "Backend report failed bounded schema validation.",
                    };
                }
            }
            stage = "verifying final backend account bracket";
            (string? Account, string? Plan, string Policy, DateTimeOffset At) after = await ReadIdentityAsync(bearer, account, token);
            if (after.Account != account)
                return Failure(rows, ServerEvidenceState.Conflict, "Backend account changed across collection; reports discarded.");
            // Backend brackets use the captured bearer. Also detect a different local account
            // selected during the fetch; two successful old-account replies cannot detect that race.
            stage = "rechecking locally selected account";
            using JsonDocument selectedAfter = JsonDocument.Parse(await _readAuth(token));
            if (selectedAfter.RootElement.GetProperty("tokens").GetProperty("account_id").GetString() != account)
                return Failure(rows, ServerEvidenceState.Conflict, "Selected Codex account changed during collection; reports discarded.");
            string key = "codex-account-sha256/v1:" +
                         Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account))).ToLowerInvariant();
            return new CodexServerCollection(
                rows.Select(row => row with
                {
                    CorrelatedAccountKey = key,
                    AccountEvidence = AccountEvidenceClass.ServerCorrelated,
                    AccountBracket = new CodexServerAccountBracket(key, before.At, key, after.At),
                    DailyReport = row.DailyReport is null
                        ? null
                        : row.DailyReport with { Plan = before.Plan, PolicyBefore = before.Policy, PolicyAfter = after.Policy },
                }).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized)
        {
            return Failure(
                rows,
                ServerEvidenceState.AuthenticationRequired,
                "Codex sign-in required; no automatic auth refresh attempted.");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Failure(
                rows,
                e is FileNotFoundException or DirectoryNotFoundException
                    ? ServerEvidenceState.AuthenticationRequired
                    : ServerEvidenceState.Error,
                $"Backend collection failed while {stage}: {(e is OperationCanceledException ? "timeout" : e is HttpRequestException ? "transport failure" : e is FileNotFoundException or DirectoryNotFoundException ? "credentials missing" : "unavailable or invalid data")}. Reports discarded; no raw error retained.");
        }
    }

    private static CodexServerObservation New(bool relative, DateTimeOffset started)
    {
        return new CodexServerObservation(
            Guid.NewGuid().ToString("N"),
            relative ? CodexServerSurface.DailyRelativeUsage : CodexServerSurface.DailyCounts,
            null,
            started,
            started,
            CodexServerEvidenceParser.Contract,
            CodexDailyReportParser.Contract,
            ServerEvidenceState.Unavailable,
            "Not collected.");
    }

    private static CodexServerCollection Failure(IEnumerable<CodexServerObservation> rows, ServerEvidenceState state, string detail)
    {
        return new CodexServerCollection(
            rows.Select(row => row with
            {
                State = state,
                Detail = detail,
                CollectedAtUtc = DateTimeOffset.UtcNow,
                DailyReport = null,
                PlanHistory = null,
                TaskUsage = null,
            }).ToArray());
    }

    private async Task<(string? Account, string? Plan, string Policy, DateTimeOffset At)> ReadIdentityAsync(
        string bearer,
        string account,
        CancellationToken token)
    {
        using JsonDocument document = JsonDocument.Parse(await GetAsync("wham/usage", bearer, account, token));
        JsonElement root = document.RootElement;
        CodexDailyReportParser.Validate(root);
        string? plan = CodexDailyReportParser.Text(root, "plan_type");
        var windows = new List<string>();
        if (root.TryGetProperty("rate_limit", out JsonElement limits) && limits.ValueKind == JsonValueKind.Object)
            foreach (string name in new[] { "primary_window", "secondary_window" })
            {
                if (limits.TryGetProperty(name, out JsonElement window) && window.ValueKind == JsonValueKind.Object)
                {
                    string duration = window.TryGetProperty("limit_window_seconds", out JsonElement d) && d.TryGetInt64(out long seconds)
                        ? seconds.ToString(CultureInfo.InvariantCulture)
                        : "unknown";
                    string reset = window.TryGetProperty("reset_at", out JsonElement r) && r.TryGetInt64(out long epoch)
                        ? epoch.ToString(CultureInfo.InvariantCulture)
                        : "unknown";
                    windows.Add($"{name}:{duration}:{reset}");
                }
            }
        return (CodexDailyReportParser.Text(root, "account_id"), plan, $"{plan}|{string.Join('|', windows)}", DateTimeOffset.UtcNow);
    }

    private Task<string> GetAsync(string route, string bearer, string account, CancellationToken token)
    {
        return SendAsync(route, bearer, account, token);
    }

    private async Task<string> SendAsync(string route, string bearer, string account, CancellationToken token, string? body = null)
    {
        using var request = new HttpRequestMessage(
            body is null ? HttpMethod.Get : HttpMethod.Post,
            "https://chatgpt.com/backend-api/" + route);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("ChatGPT-Account-ID", account);
        request.Headers.Add("originator", "codex_cli_rs");
        request.Headers.UserAgent.ParseAdd("TajsTokens/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException();
        await using Stream stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + count > MaxBytes) throw new InvalidDataException();
            buffer.Write(chunk, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}