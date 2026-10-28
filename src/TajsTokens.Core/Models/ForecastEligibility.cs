namespace TajsTokens.Core.Models;

/// <summary>Counts use the named unit. Reasons can overlap; unassessed stages have null counts.</summary>
public sealed record ForecastEligibilityStage(string Stage, string Unit, int? Entered, int? Passed,
    IReadOnlyDictionary<string, int> Reasons);

/// <summary>Only closed numeric diagnostics and aliases, never source/account/session identifiers.</summary>
public sealed record ForecastEligibilityExample(string Alias, IReadOnlyList<string> Reasons,
    int RetainedTokenEvents, int TimelyTokenEvents, int UnknownCollectionEvents, int LaterCollectedEvents,
    int RootSessions, int SubagentSessions, int UnknownRoleSessions, int MissingModelEvents, int MissingEffortEvents);

public sealed record ForecastSupportExample(string Alias, IReadOnlyList<string> Reasons, int MatchingIntervals,
    double RequestedHours, double MinimumHours, double MaximumHours, int RequestedRoots, int MinimumRoots, int MaximumRoots,
    int RequestedSubagents, int MinimumSubagents, int MaximumSubagents);

public sealed record ForecastOriginEligibility(string Cohort, string Window, string Target, string Period, DateTimeOffset FinalPeriodFromUtc,
    int CandidateOrigins, int OriginWorkloadRejected, IReadOnlyDictionary<string, int> OriginReasonOverlaps,
    IReadOnlyDictionary<string, int> InputConditionOverlaps,
    IReadOnlyList<ForecastEligibilityStage> Stages, IReadOnlyList<ForecastEligibilityStage> TrainingSelection,
    IReadOnlyList<ForecastEligibilityExample> Examples, IReadOnlyList<ForecastSupportExample> SupportExamples);

public sealed record ForecastEligibilityReport(string Version, string DatasetIdentity, DateTimeOffset CapturedAtUtc,
    IReadOnlyList<ForecastEligibilityStage> Acquisition, IReadOnlyList<ForecastOriginEligibility> Origins,
    string Boundary);

public sealed record ScenarioHistorySelection(IReadOnlyList<ScenarioHistorySample> Samples,
    ScenarioHistorySample? Latest, bool IsStale, IReadOnlyList<ForecastEligibilityStage> Stages)
{
    public string Describe()
    {
        ForecastEligibilityStage first = Stages[0];
        if (IsStale) return $"History: {first.Passed} compatible completed intervals, all older than 30 days.";
        return $"History: {first.Passed}/{first.Entered} intervals have compatible allowance/account and completed outcomes; " +
            $"{Stages[1].Passed} match the latest source/plan; {Stages[2].Passed} match the requested time/model/effort.";
    }
}
