// Taj's Tokens | CodexBackendDailyEvidenceProviderTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Net;
using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Providers;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class CodexBackendDailyEvidenceProviderTests
{

    private const string Identity = """{"account_id":"test-account","plan_type":"pro"}""";

    private static string Auth(string account, string bearer = "fake-token")
    {
        return JsonSerializer.Serialize(new { tokens = new { account_id = account, access_token = bearer } });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(30)]
    public async Task ExplicitRangePreservesQueryAndReportDates(int days)
    {
        using var handler = new Responses([Identity, """{"data":[]}""", """{"data":[]}""", Identity]);
        using var client = new HttpClient(handler);
        var provider = new CodexBackendDailyEvidenceProvider(() => true, client, _ => Task.FromResult(Auth("test-account")));
        var start = new DateOnly(2026, 1, 1);
        DateOnly end = start.AddDays(days);
        CodexServerCollection result = await provider.CollectRangeAsync(start, end, default);
        Assert.Equal(4, handler.Calls);
        Assert.All(
            handler.Queries.Skip(1).Take(2),
            query =>
                Assert.Contains($"start_date=2026-01-01&end_date={end:yyyy-MM-dd}&group_by=day", query));
        Assert.All(
            result.Observations,
            row =>
            {
                Assert.Equal("2026-01-01", row.DailyReport!.StartDate);
                Assert.Equal(end.ToString("yyyy-MM-dd"), row.DailyReport.EndDate);
            });
    }

    [Fact]
    public async Task InvalidRangesRejectBeforeCredentialsOrNetwork()
    {
        using var handler = new Responses([]);
        using var client = new HttpClient(handler);
        var provider = new CodexBackendDailyEvidenceProvider(
            () => true,
            client,
            _ => throw new InvalidOperationException("Credentials must not be read"));
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach ((DateOnly start, DateOnly end) in new[]
                 {
                     (today, today.AddDays(-1)), (today.AddDays(-31), today), (today, today.AddDays(1)),
                 })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.CollectRangeAsync(start, end, default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalAccountSwitchDiscardsReportsEvenWhenBothBackendBracketsMatch(bool change)
    {
        int reads = 0;
        using var handler = new Responses(
        [
            Identity, """{"balance_unit":"credit","data":[]}""",
            """{"units":"percent","data":[]}""", Identity,
        ]);
        using var client = new HttpClient(handler);
        var provider = new CodexBackendDailyEvidenceProvider(
            () => true,
            client,
            _ =>
                Task.FromResult(
                    Auth(++reads > 1 && change ? "different-account" : "test-account", reads > 1 ? "rotated-token" : "fake-token")));
        CodexServerCollection result = await provider.CollectAsync([], default);
        Assert.Equal(2, reads);
        Assert.Equal(4, handler.Calls);
        Assert.All(
            result.Observations,
            row =>
            {
                Assert.Equal(change ? ServerEvidenceState.Conflict : ServerEvidenceState.Empty, row.State);
                if (change)
                {
                    Assert.Null(row.DailyReport);
                    Assert.Null(row.CorrelatedAccountKey);
                }
                else
                {
                    Assert.NotNull(row.DailyReport);
                    Assert.Equal(AccountEvidenceClass.ServerCorrelated, row.AccountEvidence);
                }
            });
        string retained = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("fake-token", retained);
        Assert.DoesNotContain("test-account", retained);
        Assert.DoesNotContain("rotated-token", retained);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task UnauthorizedIdentityBracketIsAuthenticationRequiredAndNeverRefreshed(int failureAt)
    {
        using var handler = new Responses([Identity, """{"data":[]}""", """{"data":[]}""", Identity], failureAt);
        using var client = new HttpClient(handler);
        var provider = new CodexBackendDailyEvidenceProvider(() => true, client, _ => Task.FromResult(Auth("test-account")));
        CodexServerCollection result = await provider.CollectAsync([], default);
        Assert.Equal(failureAt + 1, handler.Calls);
        Assert.All(
            result.Observations,
            row =>
            {
                Assert.Equal(ServerEvidenceState.AuthenticationRequired, row.State);
                Assert.Null(row.DailyReport);
                Assert.Null(row.CorrelatedAccountKey);
            });
    }

    [Fact]
    public async Task DisabledMeansNeitherCredentialsNorNetworkAreRead()
    {
        using var handler = new Responses([]);
        using var client = new HttpClient(handler);
        var provider = new CodexBackendDailyEvidenceProvider(
            () => false,
            client,
            _ => throw new InvalidOperationException("Credentials must not be read"));
        Assert.Empty((await provider.CollectAsync([], default)).Observations);
        Assert.Empty((await provider.CollectHistoricalAsync([], default)).Observations);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalReportsUseFixedReadRoutesAndDiscardOnAccountSwitch(bool change)
    {
        int reads = 0;
        string thread = Guid.NewGuid().ToString();
        using var handler = new Responses([Identity, """{"periods":[]}""", """{"threads":[]}""", Identity]);
        using var client = new HttpClient(handler);
        var provider = new CodexBackendDailyEvidenceProvider(
            () => true,
            client,
            _ => Task.FromResult(Auth(++reads > 1 && change ? "other" : "test-account")));
        CodexServerCollection result = await provider.CollectHistoricalAsync([thread], default);
        Assert.Equal(4, handler.Calls);
        Assert.All(result.Observations, x => Assert.Equal(change ? ServerEvidenceState.Conflict : ServerEvidenceState.Empty, x.State));
        Assert.Equal(change, result.Observations[0].PlanHistory is null);
        Assert.Equal(change, result.Observations[1].TaskUsage is null);
        Assert.DoesNotContain("fake-token", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("test-account", JsonSerializer.Serialize(result));
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        JsonElement requested = Assert.Single(body.RootElement.GetProperty("threads").EnumerateArray());
        Assert.Equal(thread, requested.GetProperty("thread_id").GetString());
        Assert.Empty(requested.GetProperty("descendant_thread_ids").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, requested.GetProperty("created_at").ValueKind);
    }

    private sealed class Responses(string[] responses, int unauthorizedAt = -1) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Queries { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("chatgpt.com", request.RequestUri.Host);
            Assert.Equal(request.RequestUri.AbsolutePath.EndsWith("query_v2") ? HttpMethod.Post : HttpMethod.Get, request.Method);
            Assert.Contains(
                request.RequestUri.AbsolutePath,
                new[]
                {
                    "/backend-api/wham/usage",
                    "/backend-api/wham/analytics/daily-workspace-usage-counts",
                    "/backend-api/wham/usage/daily-token-usage-breakdown",
                    "/backend-api/wham/usage/plan_limit_history",
                    "/backend-api/wham/usage/thread_usage/query_v2",
                });
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-account", Assert.Single(request.Headers.GetValues("ChatGPT-Account-ID")));
            int index = Calls++;
            Queries.Add(request.RequestUri.Query);
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(index == unauthorizedAt ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            {
                Content = new StringContent(responses[index]),
            };
        }
    }
}