// Taj's Tokens | LocalLimitsHttpServerTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class LocalLimitsHttpServerTests
{
    [Fact]
    public async Task LoopbackEndpoint_RequiresTokenAndRejectsBrowserOriginsAndWrites()
    {
        using var reserved = new TcpListener(IPAddress.Loopback, 0);
        reserved.Start();
        int port = ((IPEndPoint)reserved.LocalEndpoint).Port;
        reserved.Stop();
        const string token = "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899";
        using var server = new LocalLimitsHttpServer(() => new LocalLimitsEnvelope(
            "tajstokens.limits.v1", DateTimeOffset.UtcNow, null, "not-collected", []));
        server.Configure(true, port, token);
        Assert.StartsWith("Listening", server.Status);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

        using HttpResponseMessage unauthenticated = await client.GetAsync("v1/limits");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage allowed = await client.GetAsync("v1/limits");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Contains("tajstokens.limits.v1", await allowed.Content.ReadAsStringAsync());
        Assert.False(allowed.Headers.Contains("Access-Control-Allow-Origin"));

        using var browserRequest = new HttpRequestMessage(HttpMethod.Get, "v1/limits");
        browserRequest.Headers.Add("Origin", "https://untrusted.example");
        using HttpResponseMessage browser = await client.SendAsync(browserRequest);
        Assert.Equal(HttpStatusCode.Forbidden, browser.StatusCode);
        using HttpResponseMessage write = await client.PostAsync("v1/limits", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, write.StatusCode);

        server.Configure(false, port, token);
        Assert.Equal("Disabled", server.Status);
    }
}
