// Taj's Tokens | CodexLiveFleetModels.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public enum CodexFleetState
{
    RecentlyWorking,
    StaleInProgress,
    ReadyForPrompt,
    Ended,
    Unknown,
}

/// <summary>Local, read-only status evidence. It is not proof that a process is alive or needs input.</summary>
public sealed record CodexLiveFleetThread(
    CodexThreadCatalogEntry Thread,
    CodexFleetState State,
    string? TurnId,
    string? NativeTurnStatus,
    DateTimeOffset? TurnStartedAtUtc,
    DateTimeOffset? LastObservedAtUtc,
    string? LastItemType,
    string? HistorySourcePath);

public sealed record CodexLiveFleetSnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<CodexLiveFleetThread> Threads,
    IReadOnlyList<string> Warnings,
    bool HistoryCapabilityAvailable,
    bool CoverageTruncated);

public static class CodexLiveFleetPolicy
{
    /// <summary>Do not surface arbitrary SQLite text as a live item category.</summary>
    public static string? NormalizeItemType(string? itemType) => itemType is
        "userMessage" or "hookPrompt" or "agentMessage" or "functionCallOutput" or
        "plan" or "reasoning" or "commandExecution" or "fileChange" or "mcpToolCall" or
        "dynamicToolCall" or "collabAgentToolCall" or "subAgentActivity" or "webSearch" or
        "imageView" or "sleep" or "imageGeneration" or "enteredReviewMode" or
        "exitedReviewMode" or "contextCompaction"
            ? itemType : null;

    public static CodexFleetState Classify(string? nativeStatus, DateTimeOffset? lastObservedAtUtc, DateTimeOffset nowUtc)
    {
        if (string.Equals(nativeStatus, "inProgress", StringComparison.OrdinalIgnoreCase))
        {
            // An interrupted process can leave this state in SQLite. Freshness is evidence of
            // recent writes, not process liveness or a verified attention-required state.
            return lastObservedAtUtc is { } observed &&
                   observed <= nowUtc.AddMinutes(1) && observed >= nowUtc.AddMinutes(-2)
                ? CodexFleetState.RecentlyWorking
                : CodexFleetState.StaleInProgress;
        }

        if (string.Equals(nativeStatus, "completed", StringComparison.OrdinalIgnoreCase))
            return CodexFleetState.ReadyForPrompt;
        if (string.Equals(nativeStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nativeStatus, "interrupted", StringComparison.OrdinalIgnoreCase))
            return CodexFleetState.Ended;
        return CodexFleetState.Unknown;
    }
}
