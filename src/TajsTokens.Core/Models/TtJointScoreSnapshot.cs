namespace TajsTokens.Core.Models;

/// <summary>Original selected scoring output or an explicitly linked restatement of exactly the same workload.</summary>
public sealed record TtJointScoreSnapshot(string Id, DateTimeOffset RecordedAtUtc, DateTimeOffset DatasetCapturedAtUtc,
    string Provider, string Profile, DateTimeOffset FromUtc, DateTimeOffset ToUtc, TtJointWorkloadBasis Basis,
    CodexJointWorkload Workload, decimal? ExactTt, string? RestatesSnapshotId = null)
{
    public const string Version = "tt-joint-score-snapshot/v1";
    public string Contract { get; init; } = Version;
}

public sealed record TtJointScoreArchiveEntry(string Id, DateTimeOffset? RecordedAtUtc, TtJointScoreSnapshot? Snapshot, string? Problem);
