// Taj's Tokens | CodexRemoteFleetModels.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

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
    bool HistoryCapabilityAvailable = false);

public static class CodexRemoteFleetHostPolicy
{
    public static bool IsValid(string? host) =>
        !string.IsNullOrWhiteSpace(host) && host.Length <= 253 &&
        char.IsLetterOrDigit(host[0]) &&
        host.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    public static string[] Normalize(IEnumerable<string>? hosts) =>
        (hosts ?? []).Where(IsValid).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
}
