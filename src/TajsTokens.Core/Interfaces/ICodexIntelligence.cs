using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

/// <summary>Product-facing Codex selection and inference boundary. Research remains separately invoked.</summary>
public interface ICodexIntelligence
{
    CodexIntelligenceSnapshot Current { get; }
    Task<CodexIntelligenceSnapshot> QueryAsync(CodexSelection selection, CancellationToken token);
    Task<CodexIntelligenceSnapshot> AnalyzeAsync(CodexIntelligenceSnapshot snapshot, CancellationToken token);
    Task<ScenarioEstimate> SimulateAsync(ScenarioRequest request, DateTimeOffset historyFromUtc, CancellationToken token);
}
