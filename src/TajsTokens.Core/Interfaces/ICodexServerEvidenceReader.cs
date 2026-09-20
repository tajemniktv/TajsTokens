using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public sealed record CodexServerEvidenceHistory(IReadOnlyList<CodexServerObservation> Observations,
    bool IsPartial, int InvalidCaptures, DateTimeOffset CapturedAtUtc);

public interface ICodexServerEvidenceReader
{
    Task<CodexServerEvidenceHistory> ReadHistoryAsync(DateTimeOffset fromUtc, DateTimeOffset asOf,
        string? accountKey, CancellationToken token);
}
