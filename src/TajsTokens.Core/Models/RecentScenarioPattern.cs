namespace TajsTokens.Core.Models;

/// <summary>Editable local-history suggestion; no account attribution or future-activity prediction.</summary>
public sealed record RecentScenarioPattern(DateTimeOffset AsOfUtc, bool Available, int RootSessions,
    int SubagentSessions, string? Model, string? ReasoningEffort, string Explanation);
