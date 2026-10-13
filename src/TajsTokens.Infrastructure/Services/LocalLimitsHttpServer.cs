// Taj's Tokens | LocalLimitsHttpServer.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Services;

/// <summary>
///     Explicitly opt-in, read-only loopback endpoint. A bearer token is required even on loopback;
///     browser Origin requests are rejected and no CORS permission is emitted.
/// </summary>
public sealed class LocalLimitsHttpServer(Func<LocalLimitsEnvelope> readLimits) : IDisposable
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private string _token = string.Empty;
    private int _port;
    private string _status = "Disabled";

    public string Status { get { lock (_sync) return _status; } }

    public void Configure(bool enabled, int port, string token)
    {
        lock (_sync)
        {
            if (!enabled)
            {
                StopCore();
                _status = "Disabled";
                return;
            }
            if (port is < 1024 or > 65535 || token.Length != 64 || !token.All(Uri.IsHexDigit))
            {
                StopCore();
                _status = "Unavailable: invalid API port or token";
                return;
            }
            if (_listener?.IsListening == true && _port == port &&
                CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(_token), Encoding.ASCII.GetBytes(token)))
                return;

            StopCore();
            HttpListener? listener = null;
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                var cancellation = new CancellationTokenSource();
                _listener = listener;
                _cancellation = cancellation;
                _port = port;
                _token = token;
                _status = $"Listening on 127.0.0.1:{port}";
                _ = Task.Run(() => AcceptLoopAsync(listener, token, cancellation.Token));
            }
            catch (Exception exception) when (exception is HttpListenerException or InvalidOperationException)
            {
                listener?.Close();
                _status = $"Unavailable: {exception.Message.ReplaceLineEndings(" ")}";
            }
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener, string token, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }

            try { await HandleAsync(context, token, cancellationToken); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A disconnected client or failed projection must not terminate the listener.
                try { context.Response.Close(); }
                catch (Exception closeError) when (closeError is HttpListenerException or ObjectDisposedException) { }
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, string token, CancellationToken cancellationToken)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        response.KeepAlive = false;
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        try
        {
            if (request.RemoteEndPoint is null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address) ||
                request.Url?.Host != "127.0.0.1" || request.Headers["Origin"] is not null)
            {
                response.StatusCode = 403;
                return;
            }
            if (request.HttpMethod != "GET")
            {
                response.StatusCode = 405;
                response.Headers["Allow"] = "GET";
                return;
            }
            if (request.Url.AbsolutePath != "/v1/limits" || request.HasEntityBody)
            {
                response.StatusCode = 404;
                return;
            }
            string? authorization = request.Headers["Authorization"];
            if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal) ||
                !TokenEquals(authorization[7..], token))
            {
                response.StatusCode = 401;
                return;
            }

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(readLimits(), s_json);
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = payload.Length;
            await response.OutputStream.WriteAsync(payload, cancellationToken);
        }
        finally { response.Close(); }
    }

    private static bool TokenEquals(string provided, string expected) =>
        provided.Length == expected.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(provided), Encoding.ASCII.GetBytes(expected));

    private void StopCore()
    {
        _cancellation?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _cancellation?.Dispose();
        _cancellation = null;
        _listener = null;
        _token = string.Empty;
        _port = 0;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            StopCore();
            _status = "Disabled";
        }
    }
}
