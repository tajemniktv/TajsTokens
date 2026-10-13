// Taj's Tokens | CodexRemoteFleetModels.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;

namespace TajsTokens.Core.Models;

public sealed record CodexRemoteFleetThread(
    string Host,
    string ThreadId,
    string NativeStatus,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? LastObservedAtUtc);

public sealed record CodexRemoteFleetHostResult(
    string Host,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<CodexRemoteFleetThread> Threads,
    string? Error,
    bool Truncated,
    int SourceErrors = 0,
    bool HistoryCapabilityAvailable = false)
{
    /// <summary>Local receive time; remote CapturedAtUtc is reported by the SSH host clock.</summary>
    public DateTimeOffset? CollectedAtUtc { get; init; }
}

/// <summary>Content-free retained check metadata; no quota or account identity is inferred.</summary>
public sealed record CodexRemoteFleetSnapshotSummary(
    string Host,
    DateTimeOffset RemoteCapturedAtUtc,
    DateTimeOffset CollectedAtUtc,
    int ThreadCount,
    bool HistoryCapabilityAvailable,
    bool Truncated,
    int SourceErrors);

public static class CodexRemoteFleetHostPolicy
{
    public static bool IsValid(string? host) =>
        !string.IsNullOrWhiteSpace(host) && host.Length <= 253 &&
        char.IsLetterOrDigit(host[0]) &&
        host.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    public static string[] Normalize(IEnumerable<string>? hosts) =>
        (hosts ?? []).Where(IsValid).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();

    public static bool IsSafeThreadId([NotNullWhen(true)] string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 128 &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static string NormalizeStatus(string? status) =>
        status is "inProgress" or "completed" or "failed" or "interrupted" or "waitingForInput"
            ? status : "unknown";
}

public static class CodexRemoteFleetFreshness
{
    public static bool WasCheckedRecently(CodexRemoteFleetHostResult snapshot, DateTimeOffset nowUtc) =>
        snapshot.CollectedAtUtc is { } collected &&
        collected <= nowUtc.AddMinutes(1) && collected >= nowUtc.AddMinutes(-2);

    /// <summary>Compare source-relative age at capture with the local receive clock, avoiding SSH host clock skew.</summary>
    public static CodexFleetState Classify(
        CodexRemoteFleetHostResult snapshot, CodexRemoteFleetThread thread, DateTimeOffset nowUtc)
    {
        DateTimeOffset? projectedObserved = null;
        if (snapshot.CollectedAtUtc is { } collected && thread.LastObservedAtUtc is { } observed)
        {
            TimeSpan ageAtCapture = snapshot.CapturedAtUtc - observed;
            if (ageAtCapture >= TimeSpan.FromMinutes(-1) && ageAtCapture <= TimeSpan.FromDays(30))
                projectedObserved = collected - ageAtCapture;
        }
        return CodexLiveFleetPolicy.Classify(thread.NativeStatus, projectedObserved, nowUtc);
    }
}
