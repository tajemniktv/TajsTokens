using System.Net;
using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Providers;

namespace TajsTokens.Core.Tests;

public sealed class CodexBackendDailyEvidenceProviderTests
{
    private static string Auth(string account, string bearer = "fake-token") =>
        JsonSerializer.Serialize(new { tokens = new { account_id = account, access_token = bearer } });
    private const string Identity = """{"account_id":"test-account","plan_type":"pro"}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalAccountSwitchDiscardsReportsEvenWhenBothBackendBracketsMatch(bool change)
    {
        var reads = 0;
        using var handler = new Responses([Identity, """{"balance_unit":"credit","data":[]}""",
            """{"units":"percent","data":[]}""", Identity]);
        using var client = new HttpClient(handler);
        var provider = new CodexBackendDailyEvidenceProvider(() => true, client, _ =>
            Task.FromResult(Auth(++reads > 1 && change ? "different-account" : "test-account", reads > 1 ? "rotated-token" : "fake-token")));
        var result = await provider.CollectAsync([], default);
        Assert.Equal(2, reads);
        Assert.Equal(4, handler.Calls);
        Assert.All(result.Observations, row =>
        {
            Assert.Equal(change ? ServerEvidenceState.Conflict : ServerEvidenceState.Empty, row.State);
            if (change) { Assert.Null(row.DailyReport); Assert.Null(row.CorrelatedAccountKey); }
            else { Assert.NotNull(row.DailyReport); Assert.Equal(AccountEvidenceClass.ServerCorrelated, row.AccountEvidence); }
        });
        var retained = JsonSerializer.Serialize(result);
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
        var result = await provider.CollectAsync([], default);
        Assert.Equal(failureAt + 1, handler.Calls);
        Assert.All(result.Observations, row =>
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
        var provider = new CodexBackendDailyEvidenceProvider(() => false, client,
            _ => throw new InvalidOperationException("Credentials must not be read"));
        Assert.Empty((await provider.CollectAsync([], default)).Observations);
        Assert.Equal(0, handler.Calls);
    }

    private sealed class Responses(string[] responses, int unauthorizedAt = -1) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("chatgpt.com", request.RequestUri.Host);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains(request.RequestUri.AbsolutePath, new[] { "/backend-api/wham/usage",
                "/backend-api/wham/analytics/daily-workspace-usage-counts", "/backend-api/wham/usage/daily-token-usage-breakdown" });
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-account", Assert.Single(request.Headers.GetValues("ChatGPT-Account-ID")));
            var index = Calls++;
            return Task.FromResult(new HttpResponseMessage(index == unauthorizedAt ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            { Content = new StringContent(responses[index]) });
        }
    }
}
