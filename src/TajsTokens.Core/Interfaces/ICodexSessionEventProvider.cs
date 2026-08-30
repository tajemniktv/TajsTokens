namespace TajsTokens.Core.Interfaces;

public interface ICodexSessionEventProvider
{
    Task<IReadOnlyList<string>> ReadNewJsonLinesAsync(string filePath, long fromOffset, CancellationToken cancellationToken);
}
