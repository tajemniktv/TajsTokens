using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ICodexSessionIngestionService
{
    Task<CodexIngestionResult> IngestAsync(string filePath, CancellationToken cancellationToken);
}
