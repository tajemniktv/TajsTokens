namespace TajsTokens.Core.Interfaces;

public interface ICodexSessionIngestionService
{
    Task<int> IngestAsync(string filePath, CancellationToken cancellationToken);
}
