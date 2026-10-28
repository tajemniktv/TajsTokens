// Taj's Tokens | CodexServerCollectionScope.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public static class CodexServerCollectionPolicy
{
    public const int MaximumThreads = 6;
    public const int RecentSourceDays = 30;
    public const int CandidateLimit = 100;
    public const int AttemptIntervalMinutes = 30;
    public const string Selection = "recent-native-source/oldest-thread-fetch/v1";
}

/// <summary>Observed request selection, not a claim of provider/report completeness.</summary>
public sealed record CodexServerCollectionScope(
    string CollectionId,
    DateTimeOffset SelectedAtUtc,
    IReadOnlyList<string> RequestedThreadIds,
    int CandidateRowsExamined,
    int EligibleThreadsInExaminedRows,
    bool CandidateScanTruncated,
    int MaximumThreads,
    int RecentSourceDays,
    int AttemptIntervalMinutes,
    string SelectionPolicy)
{
    public bool HasSupportedShape() => Guid.TryParse(CollectionId, out _) &&
        SelectionPolicy == CodexServerCollectionPolicy.Selection &&
        CandidateRowsExamined is >= 0 and <= CodexServerCollectionPolicy.CandidateLimit &&
        EligibleThreadsInExaminedRows >= 0 && EligibleThreadsInExaminedRows <= CandidateRowsExamined &&
        MaximumThreads == CodexServerCollectionPolicy.MaximumThreads &&
        RecentSourceDays == CodexServerCollectionPolicy.RecentSourceDays &&
        AttemptIntervalMinutes == CodexServerCollectionPolicy.AttemptIntervalMinutes &&
        RequestedThreadIds is not null && RequestedThreadIds.Count <= MaximumThreads &&
        RequestedThreadIds.Count <= EligibleThreadsInExaminedRows &&
        RequestedThreadIds.All(id => Guid.TryParse(id, out _)) &&
        RequestedThreadIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == RequestedThreadIds.Count;

    public string ToDisplayText()
    {
        if (!HasSupportedShape()) return "Recorded thread-selection scope is invalid or unsupported; completeness is unknown.";
        return $"Thread reports: {RequestedThreadIds.Count}/{MaximumThreads} requested; " +
        $"{EligibleThreadsInExaminedRows} eligible among {CandidateRowsExamined} examined candidate rows" +
        (CandidateScanTruncated ? " (candidate scan truncated)" : "") +
        $". Last {RecentSourceDays} days of known source activity; least recently fetched first. " +
        $"At least {AttemptIntervalMinutes} minutes between automatic attempts in this process; startup/manual checks can run sooner. " +
        $"Selected {SelectedAtUtc.ToLocalTime():g}. This sample is not complete thread or response coverage.\n" +
        "Requested thread IDs: " + (RequestedThreadIds.Count == 0 ? "none" : string.Join(", ", RequestedThreadIds));
    }

    public static string DescribeLatest(IEnumerable<CodexServerObservation> observations) => observations
        .Where(x => x.CollectionScope is not null).OrderByDescending(x => x.CollectedAtUtc)
        .Select(x => x.CollectionScope!.ToDisplayText()).FirstOrDefault()
        ?? "Thread-selection scope was not recorded for these retained captures. Completeness is unknown.";
}
