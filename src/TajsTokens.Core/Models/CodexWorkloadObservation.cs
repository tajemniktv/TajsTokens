namespace TajsTokens.Core.Models;

/// <summary>Content-free source-native rollout metadata; activity features are derived later.</summary>
public sealed record CodexWorkloadObservation(
    string SourceRecordId,
    string SourceIdentity,
    string SourceFile,
    long StartByteOffset,
    long EndByteOffset,
    string SessionId,
    string EventType,
    DateTimeOffset? ObservedAtUtc,
    DateTimeOffset CapturedAtUtc,
    string? TurnId,
    string? RootTurnId,
    string? ParentThreadId,
    string? Model,
    string? ReasoningEffort,
    long? ContextWindowTokens,
    string ContractVersion = "codex-workload/v1",
    long? StartedAtUnixSeconds = null,
    long? CompletedAtUnixSeconds = null,
    long? DurationMilliseconds = null,
    long? TimeToFirstTokenMilliseconds = null,
    string? SessionSourceKind = null);
