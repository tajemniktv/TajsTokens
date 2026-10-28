namespace TajsTokens.Core.Models;

public sealed record JointWorkloadPathStep(int SourceBin, bool StartsNewBlock,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, CodexJointWorkload Workload);

/// <summary>One immutable conditional path. Source draws are not new observations.</summary>
public sealed class JointWorkloadPath
{
    public JointWorkloadPath(IEnumerable<JointWorkloadPathStep> steps, CodexJointWorkload aggregate)
    { Steps = Array.AsReadOnly(steps.ToArray()); Aggregate = aggregate; }
    public IReadOnlyList<JointWorkloadPathStep> Steps { get; }
    public CodexJointWorkload Aggregate { get; }
}

public sealed class JointWorkloadPaths
{
    public JointWorkloadPaths(string version, string revision, uint seed, int sourceBins, int maximumBlockBins,
        WorkloadContinuationReceipt context, IEnumerable<JointWorkloadPath> paths, CodexJointWorkload? mean, string methodology)
    {
        Version = version; InputRevision = revision; Seed = seed; SourceBins = sourceBins; MaximumBlockBins = maximumBlockBins;
        Context = context; Paths = Array.AsReadOnly(paths.ToArray()); Mean = mean; Methodology = methodology;
    }
    public string Version { get; }
    public string InputRevision { get; }
    public uint Seed { get; }
    public int SourceBins { get; }
    public int MaximumBlockBins { get; }
    public WorkloadContinuationReceipt Context { get; }
    public IReadOnlyList<JointWorkloadPath> Paths { get; }
    public CodexJointWorkload? Mean { get; }
    public string Methodology { get; }
}
