using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TajsTokens.Core.Models;

[Flags]
public enum TtJointDimensions { Model = 1, ReasoningEffort = 2, ExecutionTier = 4, Product = 8, ContextBand = 16 }
public enum TtTokenCategory { UncachedInput, CacheRead, CacheWrite, NonReasoningOutput, ReasoningOutput }
public sealed record TtJointWeight(CodexWorkloadCoordinate Coordinate, TtTokenCategory Category, decimal Weight);
public sealed record TtJointReferenceAmount(CodexWorkloadCoordinate Coordinate, TtTokenCategory Category, decimal Tokens);

/// <summary>A defined, frozen consumption ruler. Construction never activates it or modifies a price catalog.</summary>
public sealed class TtJointWorkloadBasis
{
    public const string Version = "tt-joint-basis/v1";
    public const int MaximumAmounts = CodexJointWorkload.MaximumCoordinates * 5;
    private static readonly TtTokenCategory[] Categories = Enum.GetValues<TtTokenCategory>();
    private readonly Dictionary<(CodexWorkloadCoordinate, TtTokenCategory), decimal> _weights;

    [JsonConstructor]
    public TtJointWorkloadBasis(string contract, TtJointDimensions dimensions, string convention, string weightUnit,
        string vintage, IReadOnlyList<string> sourceSnapshotIds, IReadOnlyList<TtJointWeight> weights,
        IReadOnlyList<TtJointReferenceAmount> reference, string? parentBasisId = null)
    {
        if (contract != Version || !dimensions.HasFlag(TtJointDimensions.Model) || ((int)dimensions & ~31) != 0 ||
            !Label(convention, 2048) || !Label(weightUnit, 128) || !Label(vintage, 128) ||
            sourceSnapshotIds is null || sourceSnapshotIds.Count is < 1 or > 32 || sourceSnapshotIds.Any(x => !Label(x, 256)) ||
            weights is null || weights.Count is < 1 or > MaximumAmounts || reference is null || reference.Count is < 1 or > MaximumAmounts ||
            parentBasisId is not null && (!parentBasisId.StartsWith("tt-joint:", StringComparison.Ordinal) || parentBasisId.Length != 73))
            throw new ArgumentException("Unsupported or incomplete joint TT basis definition.");
        Contract = contract; Dimensions = dimensions; Convention = convention; WeightUnit = weightUnit; Vintage = vintage;
        SourceSnapshotIds = Array.AsReadOnly(sourceSnapshotIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        ParentBasisId = parentBasisId;
        if (weights.Any(x => x is null || !ValidCoordinate(x.Coordinate) || !Enum.IsDefined(x.Category) || x.Weight < 0) ||
            reference.Any(x => x is null || !ValidCoordinate(x.Coordinate) || !Enum.IsDefined(x.Category) || x.Tokens < 0))
            throw new ArgumentException("Basis coordinates must name active dimensions and nonnegative amounts.");
        _weights = new();
        foreach (var weight in weights)
            if (!_weights.TryAdd((weight.Coordinate, weight.Category), weight.Weight))
                throw new ArgumentException("Duplicate joint TT weight.");
        if (weights.Select(x => x.Coordinate).Distinct().Count() > CodexJointWorkload.MaximumCoordinates)
            throw new ArgumentException("Joint TT coordinate vocabulary exceeds its bound.");
        Weights = Array.AsReadOnly(weights.OrderBy(x => CoordinateKey(x.Coordinate), StringComparer.Ordinal).ThenBy(x => x.Category).ToArray());
        try
        {
            Reference = Array.AsReadOnly(reference.Where(x => x.Tokens > 0).GroupBy(x => (x.Coordinate, x.Category))
                .Select(g => new TtJointReferenceAmount(g.Key.Coordinate, g.Key.Category, g.Sum(x => x.Tokens)))
                .OrderBy(x => CoordinateKey(x.Coordinate), StringComparer.Ordinal).ThenBy(x => x.Category).ToArray());
            decimal normalizer = 0;
            foreach (var amount in Reference)
            {
                if (!_weights.TryGetValue((amount.Coordinate, amount.Category), out decimal weight))
                    throw new ArgumentException("Reference basket contains an unsupported coordinate/category.");
                normalizer += amount.Tokens * weight;
            }
            if (normalizer <= 0) throw new ArgumentException("Reference basket must have positive consumption weight.");
            ReferenceScore = normalizer;
        }
        catch (OverflowException ex) { throw new ArgumentException("Reference basket exceeds decimal arithmetic.", ex); }

        // G29 removes decimal scale: 1 and 1.00 define the same numeric ruler in every culture.
        string canonical = JsonSerializer.Serialize(new { Contract, Dimensions, Convention, WeightUnit, Vintage, SourceSnapshotIds, ParentBasisId,
            Weights = Weights.Select(x => new { x.Coordinate, x.Category, Weight = Number(x.Weight) }),
            Reference = Reference.Select(x => new { x.Coordinate, x.Category, Tokens = Number(x.Tokens) }) });
        BasisId = "tt-joint:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public string Contract { get; }
    public string BasisId { get; }
    public TtJointDimensions Dimensions { get; }
    public string Convention { get; }
    public string WeightUnit { get; }
    public string Vintage { get; }
    public IReadOnlyList<string> SourceSnapshotIds { get; }
    public IReadOnlyList<TtJointWeight> Weights { get; }
    public IReadOnlyList<TtJointReferenceAmount> Reference { get; }
    public decimal ReferenceScore { get; }
    public string? ParentBasisId { get; }

    /// <summary>Only ignored dimensions are projected away. An unknown active label is never a wildcard weight.</summary>
    public bool TryWeight(CodexWorkloadCoordinate coordinate, TtTokenCategory category, out decimal weight) =>
        _weights.TryGetValue((Project(coordinate), category), out weight);

    /// <summary>Exact under this named basis and the supplied reconciled quantities; no completeness or billing claim.</summary>
    public decimal? ScoreExact(CodexJointWorkload workload)
    {
        if (!workload.IsComplete || workload.Overflow is { CategoryTotal: > 0 }) return null;
        try
        {
            decimal weighted = 0;
            foreach (var cell in workload.Cells)
            foreach (TtTokenCategory category in Categories)
            {
                decimal tokens = Amount(cell, category);
                if (tokens == 0) continue;
                if (!TryWeight(cell.Coordinate!, category, out decimal weight)) return null;
                weighted += tokens * weight;
            }
            return weighted / ReferenceScore;
        }
        catch (OverflowException) { return null; }
    }

    /// <summary>Explicit additive extension: old support, weights, basket and normalization are unchanged.</summary>
    public TtJointWorkloadBasis Extend(IReadOnlyList<TtJointWeight> additions, string sourceSnapshotId)
    {
        if (additions is null || additions.Count == 0) throw new ArgumentException("An extension must add support.");
        return new(Contract, Dimensions, Convention, WeightUnit, Vintage, SourceSnapshotIds.Append(sourceSnapshotId).ToArray(),
            Weights.Concat(additions).ToArray(), Reference, BasisId);
    }

    public bool IsCompatibleExtensionOf(TtJointWorkloadBasis parent) => ParentBasisId == parent.BasisId &&
        Dimensions == parent.Dimensions && Convention == parent.Convention && WeightUnit == parent.WeightUnit && Vintage == parent.Vintage &&
        ReferenceScore == parent.ReferenceScore && Reference.SequenceEqual(parent.Reference) && Weights.Count > parent.Weights.Count &&
        parent.Weights.All(x => _weights.TryGetValue((x.Coordinate, x.Category), out decimal value) && value == x.Weight) &&
        parent.SourceSnapshotIds.All(x => SourceSnapshotIds.Contains(x, StringComparer.Ordinal));

    public static decimal Amount(CodexJointWorkloadCell cell, TtTokenCategory category) => category switch
    {
        TtTokenCategory.UncachedInput => cell.UncachedInput, TtTokenCategory.CacheRead => cell.CacheRead,
        TtTokenCategory.CacheWrite => cell.CacheWrite, TtTokenCategory.NonReasoningOutput => cell.NonReasoningOutput,
        TtTokenCategory.ReasoningOutput => cell.ReasoningOutput, _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };
    private CodexWorkloadCoordinate Project(CodexWorkloadCoordinate c) => new(c.Model,
        Dimensions.HasFlag(TtJointDimensions.ReasoningEffort) ? c.ReasoningEffort : null,
        Dimensions.HasFlag(TtJointDimensions.ExecutionTier) ? c.ExecutionTier : null,
        Dimensions.HasFlag(TtJointDimensions.Product) ? c.Product : null,
        Dimensions.HasFlag(TtJointDimensions.ContextBand) ? c.ContextBand : null);
    private bool ValidCoordinate(CodexWorkloadCoordinate? c) => c is not null && Label(c.Model, 256) &&
        Active(TtJointDimensions.ReasoningEffort, c.ReasoningEffort) && Active(TtJointDimensions.ExecutionTier, c.ExecutionTier) &&
        Active(TtJointDimensions.Product, c.Product) && Active(TtJointDimensions.ContextBand, c.ContextBand);
    private bool Active(TtJointDimensions dimension, string? value) => Dimensions.HasFlag(dimension) ? Label(value, 256) : value is null;
    private static bool Label(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    private static string CoordinateKey(CodexWorkloadCoordinate coordinate) => JsonSerializer.Serialize(coordinate);
    private static string Number(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
}
