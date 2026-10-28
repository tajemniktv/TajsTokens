using TajsTokens.Core.Services;
using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

/// <summary>Conditional continuation of installation-local work; account selection scopes only quota.</summary>
public sealed record RecentWorkPlanRequest(double DurationHours, double ContextHours = 2,
    string? Model = null, string? ReasoningEffort = null, string? AccountKey = null);

public sealed record RecentWorkPlanWindow(QuotaWindowKind Kind, ScenarioWindowEstimate Pace,
    CurrentQuotaForecast? Reading, SessionPlanWindow Assessment);

public sealed record RecentWorkPlan(DateTimeOffset EvaluatedAtUtc, RecentWorkPlanRequest Request,
    WorkloadContinuationResult Continuation, WorkloadReferenceResult? Reference,
    WorkloadReferenceResult? Revaluation, bool HasEdits, string EditStatus,
    IReadOnlyList<RecentWorkPlanWindow> Windows)
{
    public const string Version = "recent-work-plan/v1";
    public const string Methodology = "Continue installation-local joint work at its recorded elapsed-context intensity, including quiet time and tool waits. " +
        "Missing records do not establish idleness or complete account coverage. Native work and TT are conditional amounts, not new observations. " +
        "Model/effort edits hold all native category amounts fixed for reference revaluation only; no task-equivalent token prediction or extra effort multiplier. " +
        "Quota is a separate constant-current-account-pace scenario, not a token-to-quota conversion. It does not respond to local model, effort or count edits. " +
        "No calibrated range is inherited from a different horizon or reset target. Sessions crossing a reset make no current-window fit or post-reset capacity claim. " +
        "Work-history filters do not allocate quota; counts and history lookback belong to the separate count comparator.";
}
