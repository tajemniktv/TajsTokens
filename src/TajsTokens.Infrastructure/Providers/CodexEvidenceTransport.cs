using System.Text;
using System.Text.Json;

namespace TajsTokens.Infrastructure.Providers;

/// <summary>Session-owned buffering: preserve bytes after a newline for the next RPC message.</summary>
public sealed class CodexEvidenceTransport(TextReader reader, TextWriter writer)
{
    public const int MaxLineCharacters = 2 * 1024 * 1024;
    private readonly char[] _buffer = new char[4096];
    private int _position, _length;

    public async Task<string> ReadLineAsync(CancellationToken token)
    {
        var line = new StringBuilder();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (_position == _length)
            {
                _length = await reader.ReadAsync(_buffer.AsMemory(), token);
                _position = 0;
                if (_length == 0) throw new IOException("App-server closed output before a complete message.");
            }
            var end = Array.IndexOf(_buffer, '\n', _position, _length - _position);
            var count = (end < 0 ? _length : end) - _position;
            if (line.Length + count > MaxLineCharacters) throw new IOException("App-server response exceeds evidence bound.");
            line.Append(_buffer, _position, count);
            _position += count;
            if (end < 0) continue;
            _position++;
            if (line.Length > 0 && line[^1] == '\r') line.Length--;
            return line.ToString();
        }
    }

    public async Task<bool> RejectServerRequestAsync(JsonElement message, CancellationToken token)
    {
        if (!message.TryGetProperty("method", out _) || !message.TryGetProperty("id", out var id) ||
            message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _)) return false;
        // IDs are the only echoed data. Do not reflect method/params or accept unbounded identifiers.
        if (id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) || id.GetRawText().Length > 256)
            throw new IOException("Unsupported server request identity.");
        var response = JsonSerializer.Serialize(new { id, error = new { code = -32601, message = "Method not supported" } });
        await writer.WriteLineAsync(response.AsMemory(), token);
        await writer.FlushAsync(token);
        return true;
    }
}
