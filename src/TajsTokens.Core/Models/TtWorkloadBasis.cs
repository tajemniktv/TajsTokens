using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TajsTokens.Core.Models;

/// <summary>Immutable research scoring contract; never a quota balance or a universal rate card.</summary>
public sealed class TtWorkloadBasis
{
    public const string Semantics = "tt-category-basis/v1: uncached,cache-read,cache-write,nonreasoning-output,reasoning-output; disjoint token counts; pooled model/effort support; tier/context unmodelled";
    public string BasisId { get; }
    public string InputSemantics => Semantics;
    public IReadOnlyList<double> Weights { get; }
    public IReadOnlyList<double> Reference { get; }
    public IReadOnlyList<bool> SupportedCategories { get; }
    public IReadOnlyList<string> Models { get; }
    public IReadOnlyList<string> Efforts { get; }
    public double ReferenceScore { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public TtWorkloadBasis(IReadOnlyList<double> weights, IReadOnlyList<double> reference,
        IReadOnlyList<bool> supportedCategories, IReadOnlyList<string> models, IReadOnlyList<string> efforts)
        : this((IEnumerable<double>)weights, reference, supportedCategories, models, efforts) { }

    public TtWorkloadBasis(IEnumerable<double> weights, IEnumerable<double> reference,
        IEnumerable<bool> supportedCategories, IEnumerable<string> models, IEnumerable<string> efforts)
    {
        Weights = Array.AsReadOnly(weights.ToArray());
        Reference = Array.AsReadOnly(reference.ToArray());
        SupportedCategories = Array.AsReadOnly(supportedCategories.ToArray());
        Models = Array.AsReadOnly(models.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        Efforts = Array.AsReadOnly(efforts.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        if (Weights.Count != 5 || Reference.Count != 5 || SupportedCategories.Count != 5 ||
            Weights.Concat(Reference).Any(x => !double.IsFinite(x) || x < 0) ||
            Reference.Where((x, i) => x > 0 && !SupportedCategories[i]).Any())
            throw new ArgumentException("TT requires five finite nonnegative disjoint categories and a supported reference.");
        ReferenceScore = Reference.Select((x, i) => x * Weights[i]).Sum();
        if (!double.IsFinite(ReferenceScore) || ReferenceScore <= 0)
            throw new ArgumentException("TT reference must have a finite positive score.");
        var canonical = JsonSerializer.Serialize(new { Semantics, Weights, Reference, SupportedCategories, Models, Efforts });
        BasisId = "tt:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public double? Score(IReadOnlyList<double> categories)
    {
        if (categories.Count != 5 || categories.Where((x, i) => !double.IsFinite(x) || x < 0 || x > 0 && !SupportedCategories[i]).Any()) return null;
        var value = categories.Select((x, i) => x * Weights[i]).Sum() / ReferenceScore;
        return double.IsFinite(value) ? value : null;
    }

    public double? Score(QuotaCostObservation row)
    {
        // Reject missing/unsupported mix rather than making unpriced work disappear into a total.
        if (!CompleteCategories(row)) return null;
        if (row.Features.Tokens > 0 && (!Supported(row.Features.ModelTokenShares, Models) ||
            !Supported(row.Features.EffortTokenShares, Efforts))) return null;
        return Score(row.TokenCategories);
    }

    internal static bool CompleteCategories(QuotaCostObservation row) => row.Features.Tokens >= 0 &&
        row.TokenCategories.Count == 5 && row.TokenCategories.All(x => double.IsFinite(x) && x >= 0) &&
        Math.Abs(row.TokenCategories.Sum() - row.Features.Tokens) < .001;

    private static bool Supported(IReadOnlyDictionary<string, double> shares, IReadOnlyList<string> support) =>
        shares.Count > 0 && shares.All(x => double.IsFinite(x.Value) && x.Value >= 0 && support.Contains(x.Key)) &&
        Math.Abs(shares.Values.Sum() - 1) < .001;
}
