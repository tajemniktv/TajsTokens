using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ICodexRolloutInspection
{
    Task<CodexRolloutInspectionReport> InspectAlternateRolloutsAsync(int offset, CancellationToken cancellationToken);
}
