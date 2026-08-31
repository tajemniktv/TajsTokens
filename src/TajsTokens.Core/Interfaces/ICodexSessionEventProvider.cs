using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ICodexSessionEventProvider
{
    IAsyncEnumerable<RawSessionRecord> ReadNewJsonLinesAsync(
        string filePath,
        long fromOffset,
        CancellationToken cancellationToken);
}
