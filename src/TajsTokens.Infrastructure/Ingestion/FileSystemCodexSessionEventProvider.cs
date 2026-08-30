using System.Text;
using TajsTokens.Core.Interfaces;

namespace TajsTokens.Infrastructure.Ingestion;

public sealed class FileSystemCodexSessionEventProvider : ICodexSessionEventProvider
{
    public async Task<IReadOnlyList<string>> ReadNewJsonLinesAsync(string filePath, long fromOffset, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        var lines = new List<string>();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fromOffset > stream.Length)
        {
            fromOffset = 0;
        }

        stream.Seek(fromOffset, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: true);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line);
            }
        }

        return lines;
    }
}
