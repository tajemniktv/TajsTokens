using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ICodexTokenAccountingProvider
{
    Task<CodexTokenAccountingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
