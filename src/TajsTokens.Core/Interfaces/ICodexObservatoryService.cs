using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ICodexObservatoryService
{
    Task<CodexObservatoryRefreshResult> RefreshAsync(CancellationToken cancellationToken);
}
