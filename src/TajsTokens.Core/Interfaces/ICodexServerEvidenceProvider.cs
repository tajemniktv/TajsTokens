using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ICodexServerEvidenceProvider
{
    Task<CodexServerCollection> CollectAsync(IReadOnlyList<string> threadIds, CancellationToken cancellationToken);
}
