using System.Text.Json.Serialization;

namespace TajsTokens.Core.Models;

public enum TtScoreState { Exact, FiniteBounds, Partial, Unbounded, Inconsistent, ArithmeticUnavailable }

/// <summary>Nonnegative disjoint quantities. Group totals are input=uncached+read+write and output=nonreasoning+reasoning.
/// Null means unconstrained, never zero. This DTO does not reinterpret native cumulative counter snapshots.</summary>
public sealed record TtQuantityConstraints(CodexWorkloadCoordinate? Coordinate,
    decimal? UncachedInput = null, decimal? CacheRead = null, decimal? CacheWrite = null,
    decimal? NonReasoningOutput = null, decimal? ReasoningOutput = null,
    decimal? InputTotal = null, decimal? OutputTotal = null, decimal? ReportedTotal = null,
    bool SourceInconsistent = false);

/// <summary>Identification under a frozen ruler, not probability, billing, quota or account-wide coverage.</summary>
public sealed record TtScoreAssessment(string Contract, string BasisId, CodexJointQuantityKind Kind, TtScoreState State,
    decimal? LowerTt, decimal? UpperTt, decimal? CoveredSubtotalTt, decimal? TotalTokens,
    decimal? KnownUnmappedTokens, int ConstraintRows, int UnmappedRows, int InconsistentRows)
{
    [JsonIgnore] public decimal? ExactTt => State == TtScoreState.Exact ? LowerTt : null;
    [JsonIgnore] public string Explanation => State switch
    {
        TtScoreState.Exact => "Exact under the named basis and supplied quantity constraints; not proof of complete account coverage.",
        TtScoreState.FiniteBounds => "Deterministic feasible bounds under the named basis; not a confidence interval. No split was imputed.",
        TtScoreState.Partial => "Only the identified supported subtotal is scored. Missing coordinate/category weights leave the whole score undefined; no zero weight or infinite price was assigned.",
        TtScoreState.Unbounded => "The supported score has no finite upper bound under the supplied quantity constraints; more quantities are required.",
        TtScoreState.Inconsistent => "The supplied nonnegative quantities and totals are inconsistent. No whole score or subtotal is asserted.",
        _ => "The calculation exceeds supported decimal arithmetic. This does not establish inconsistent source data.",
    };
}
