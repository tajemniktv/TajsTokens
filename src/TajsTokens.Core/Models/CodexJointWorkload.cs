using System.Text.Json;
using System.Text.Json.Serialization;

namespace TajsTokens.Core.Models;

public enum CodexJointQuantityKind { ObservedIncrements, ConditionalProjection }

/// <summary>Observed labels only. Null dimensions are unknown, never a default billed tier or short context.</summary>
public sealed record CodexWorkloadCoordinate(string? Model, string? ReasoningEffort,
    string? ExecutionTier = null, string? Product = null, string? ContextBand = null);

/// <summary>One sparse coordinate row containing the five disjoint native categories and independent reported total.</summary>
public sealed record CodexJointWorkloadCell(CodexWorkloadCoordinate? Coordinate, decimal UncachedInput,
    decimal CacheRead, decimal CacheWrite, decimal NonReasoningOutput, decimal ReasoningOutput, decimal ReportedTotal,
    int EvidenceObservations, int InconsistentObservations, int InvalidObservations)
{
    [JsonIgnore] public decimal CategoryTotal => UncachedInput + CacheRead + CacheWrite + NonReasoningOutput + ReasoningOutput;
    [JsonIgnore] public bool IsComplete => InconsistentObservations == 0 && InvalidObservations == 0 &&
        UncachedInput >= 0 && CacheRead >= 0 && CacheWrite >= 0 && NonReasoningOutput >= 0 && ReasoningOutput >= 0 && ReportedTotal >= 0 &&
        Math.Abs(CategoryTotal - ReportedTotal) <= .000000001m;
}

/// <summary>Immutable bounded read projection. Overflow retains amounts but does not invent coordinate labels.</summary>
public sealed class CodexJointWorkload : IEquatable<CodexJointWorkload>
{
    public const string Version = "codex-joint-workload/v1";
    public const int MaximumCoordinates = 256;

    [JsonConstructor]
    public CodexJointWorkload(string contract, CodexJointQuantityKind kind, IReadOnlyList<CodexJointWorkloadCell> cells,
        CodexJointWorkloadCell? overflow, decimal missingModelTokens, decimal missingEffortTokens)
    {
        if (contract != Version || !Enum.IsDefined(kind) || cells is null || cells.Count > MaximumCoordinates || cells.Any(x => x is null || x.Coordinate is null) ||
            cells.Select(x => x.Coordinate).Distinct().Count() != cells.Count || overflow?.Coordinate is not null)
            throw new JsonException("Unsupported or inconsistent joint-workload projection.");
        Contract = contract; Kind = kind; Cells = Array.AsReadOnly(cells.ToArray()); Overflow = overflow;
        MissingModelTokens = missingModelTokens; MissingEffortTokens = missingEffortTokens;
    }

    public string Contract { get; }
    public CodexJointQuantityKind Kind { get; }
    public IReadOnlyList<CodexJointWorkloadCell> Cells { get; }
    public CodexJointWorkloadCell? Overflow { get; }
    public decimal MissingModelTokens { get; }
    public decimal MissingEffortTokens { get; }
    [JsonIgnore] public decimal ReportedTokens => Cells.Sum(x => x.ReportedTotal) + (Overflow?.ReportedTotal ?? 0);
    [JsonIgnore] public decimal CategoryTokens => Cells.Sum(x => x.CategoryTotal) + (Overflow?.CategoryTotal ?? 0);
    [JsonIgnore] public int EvidenceObservations => Cells.Sum(x => x.EvidenceObservations) + (Overflow?.EvidenceObservations ?? 0);
    [JsonIgnore] public int InconsistentObservations => Cells.Sum(x => x.InconsistentObservations) + (Overflow?.InconsistentObservations ?? 0);
    [JsonIgnore] public int InvalidObservations => Cells.Sum(x => x.InvalidObservations) + (Overflow?.InvalidObservations ?? 0);
    [JsonIgnore] public bool IsComplete => Cells.All(x => x.IsComplete) && (Overflow?.IsComplete ?? true);

    public string Describe() => $"Joint native workload: {Cells.Count:N0} coordinate rows; {ReportedTokens:N0} reported tokens; " +
        $"{MissingModelTokens:N0} missing-model and {MissingEffortTokens:N0} missing-effort tokens (overlapping). " +
        $"{Overflow?.ReportedTotal ?? 0:N0} tokens outside the {MaximumCoordinates}-coordinate vocabulary; retained separately from missing labels. " +
        $"{InconsistentObservations:N0} category-inconsistent / {InvalidObservations:N0} invalid observations. " +
        "Native token rows do not establish execution tier, billing product or charged context band; those coordinates remain unknown. " +
        (Kind == CodexJointQuantityKind.ObservedIncrements ? "Selected local increments; no account-wide attribution or TT conversion." :
            "Conditional continuation of the observed joint mixture, not new source observations.");

    public bool Equals(CodexJointWorkload? other) => other is not null && Contract == other.Contract && Kind == other.Kind &&
        MissingModelTokens == other.MissingModelTokens && MissingEffortTokens == other.MissingEffortTokens &&
        Cells.SequenceEqual(other.Cells) && Overflow == other.Overflow;
    public override bool Equals(object? obj) => obj is CodexJointWorkload other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(Contract); hash.Add(Kind); hash.Add(MissingModelTokens); hash.Add(MissingEffortTokens);
        foreach (var cell in Cells) hash.Add(cell);
        hash.Add(Overflow); return hash.ToHashCode();
    }
}
