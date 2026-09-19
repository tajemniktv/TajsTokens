using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Providers;

/// <summary>Opt-in, fixed-origin, read-only backend adapter. Never refreshes or persists credentials.</summary>
public sealed class CodexBackendDailyEvidenceProvider : ICodexServerEvidenceProvider
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(20) };
    private const int MaxBytes = 4 * 1024 * 1024;
    private readonly Func<bool> _enabled;
    private readonly HttpClient _client;
    private readonly Func<CancellationToken, Task<string>> _readAuth;

    public CodexBackendDailyEvidenceProvider(Func<bool> enabled) : this(enabled, Client, ReadAuthAsync) { }

    internal CodexBackendDailyEvidenceProvider(Func<bool> enabled, HttpClient client,
        Func<CancellationToken, Task<string>> readAuth)
    {
        _enabled = enabled;
        _client = client;
        _readAuth = readAuth;
    }

    private static async Task<string> ReadAuthAsync(CancellationToken token)
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var path = Path.Combine(home, "auth.json");
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException();
        return await File.ReadAllTextAsync(path, token);
    }

    public Task<CodexServerCollection> CollectAsync(IReadOnlyList<string> threadIds, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return CollectRangeAsync(today.AddDays(-30), today, cancellationToken);
    }

    /// <summary>Explicit bounded research range; dates are sent verbatim, without assuming endpoint inclusivity.</summary>
    public async Task<CodexServerCollection> CollectRangeAsync(DateOnly startDate, DateOnly endDate,
        CancellationToken cancellationToken)
    {
        if (endDate < startDate || endDate.DayNumber - startDate.DayNumber > 30 ||
            endDate > DateOnly.FromDateTime(DateTime.UtcNow))
            throw new ArgumentOutOfRangeException(nameof(endDate), "Range must be ordered, at most 30 days apart, and not future-dated.");
        if (!_enabled()) return new([]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        var started = DateTimeOffset.UtcNow;
        var start = startDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var end = endDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var query = $"?start_date={start}&end_date={end}&group_by=day";
        var routes = new[] { "wham/analytics/daily-workspace-usage-counts", "wham/usage/daily-token-usage-breakdown" };
        var rows = routes.Select((_, index) => New(index == 1, started)).ToArray();
        try
        {
            using var auth = JsonDocument.Parse(await _readAuth(token));
            var credentials = auth.RootElement.GetProperty("tokens");
            var bearer = credentials.GetProperty("access_token").GetString();
            var account = credentials.GetProperty("account_id").GetString();
            if (string.IsNullOrWhiteSpace(bearer) || string.IsNullOrWhiteSpace(account)) throw new InvalidDataException();
            var before = await ReadIdentityAsync(bearer, account, token);
            if (before.Account != account) return Failure(rows, ServerEvidenceState.Conflict, "Backend account does not match selected Codex credentials.");
            for (var i = 0; i < routes.Length; i++)
            {
                var fetch = DateTimeOffset.UtcNow;
                try
                {
                    var json = await GetAsync(routes[i] + query + (i == 0 ? "&workspace_user=true" : ""), bearer, account, token);
                    var report = CodexDailyReportParser.Parse(json, i == 1, routes[i], start, end);
                    rows[i] = rows[i] with { FetchStartedAtUtc = fetch, CollectedAtUtc = DateTimeOffset.UtcNow,
                        State = report.Days.Count == 0 ? ServerEvidenceState.Empty : ServerEvidenceState.Available,
                        Detail = "Experimental backend daily snapshot; not live quota or billing-cycle consumption.", DailyReport = report };
                }
                catch (HttpRequestException e)
                {
                    rows[i] = rows[i] with { FetchStartedAtUtc = fetch, CollectedAtUtc = DateTimeOffset.UtcNow,
                        State = e.StatusCode == HttpStatusCode.Unauthorized ? ServerEvidenceState.AuthenticationRequired : ServerEvidenceState.Unavailable,
                        Detail = e.StatusCode is null ? "Backend transport failed." : $"Backend HTTP {(int)e.StatusCode}." };
                    if (e.StatusCode == HttpStatusCode.Unauthorized) return Failure(rows, ServerEvidenceState.AuthenticationRequired, "Codex sign-in required; no automatic auth refresh attempted.");
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
                {
                    rows[i] = rows[i] with { FetchStartedAtUtc = fetch, CollectedAtUtc = DateTimeOffset.UtcNow,
                        State = ServerEvidenceState.Invalid, Detail = "Daily report failed bounded schema validation." };
                }
            }
            var after = await ReadIdentityAsync(bearer, account, token);
            if (after.Account != account) return Failure(rows, ServerEvidenceState.Conflict, "Backend account changed across collection; reports discarded.");
            // Backend brackets use the captured bearer. Also detect a different local account
            // selected during the fetch; two successful old-account replies cannot detect that race.
            using var selectedAfter = JsonDocument.Parse(await _readAuth(token));
            if (selectedAfter.RootElement.GetProperty("tokens").GetProperty("account_id").GetString() != account)
                return Failure(rows, ServerEvidenceState.Conflict, "Selected Codex account changed during collection; reports discarded.");
            var key = "codex-account-sha256/v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account))).ToLowerInvariant();
            return new(rows.Select(row => row with { CorrelatedAccountKey = key, AccountEvidence = AccountEvidenceClass.ServerCorrelated,
                AccountBracket = new(key, before.At, key, after.At),
                DailyReport = row.DailyReport is null ? null : row.DailyReport with { Plan = before.Plan, PolicyBefore = before.Policy, PolicyAfter = after.Policy } }).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized)
        {
            return Failure(rows, ServerEvidenceState.AuthenticationRequired, "Codex sign-in required; no automatic auth refresh attempted.");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Failure(rows, e is FileNotFoundException or DirectoryNotFoundException ? ServerEvidenceState.AuthenticationRequired : ServerEvidenceState.Error,
                "Backend collection could not verify credentials/account or complete within bounds; no raw error retained.");
        }
    }

    private static CodexServerObservation New(bool relative, DateTimeOffset started) => new(Guid.NewGuid().ToString("N"),
        relative ? CodexServerSurface.DailyRelativeUsage : CodexServerSurface.DailyCounts, null,
        started, started, CodexServerEvidenceParser.Contract, CodexDailyReportParser.Contract, ServerEvidenceState.Unavailable, "Not collected.");

    private static CodexServerCollection Failure(IEnumerable<CodexServerObservation> rows, ServerEvidenceState state, string detail) =>
        new(rows.Select(row => row with { State = state, Detail = detail, CollectedAtUtc = DateTimeOffset.UtcNow, DailyReport = null }).ToArray());

    private async Task<(string? Account, string? Plan, string Policy, DateTimeOffset At)> ReadIdentityAsync(string bearer, string account, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await GetAsync("wham/usage", bearer, account, token));
        var root = document.RootElement;
        CodexDailyReportParser.Validate(root);
        var plan = CodexDailyReportParser.Text(root, "plan_type");
        var windows = new List<string>();
        if (root.TryGetProperty("rate_limit", out var limits) && limits.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "primary_window", "secondary_window" })
                if (limits.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object)
                {
                    var duration = window.TryGetProperty("limit_window_seconds", out var d) && d.TryGetInt64(out var seconds) ? seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
                    var reset = window.TryGetProperty("reset_at", out var r) && r.TryGetInt64(out var epoch) ? epoch.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
                    windows.Add($"{name}:{duration}:{reset}");
                }
        return (CodexDailyReportParser.Text(root, "account_id"), plan, $"{plan}|{string.Join('|', windows)}", DateTimeOffset.UtcNow);
    }

    private async Task<string> GetAsync(string route, string bearer, string account, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/" + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("ChatGPT-Account-ID", account);
        request.Headers.Add("originator", "codex_cli_rs");
        request.Headers.UserAgent.ParseAdd("TajsTokens/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + count > MaxBytes) throw new InvalidDataException();
            buffer.Write(chunk, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
