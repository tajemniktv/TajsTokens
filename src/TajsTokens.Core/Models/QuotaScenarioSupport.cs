using System.Text.Json.Serialization;

namespace TajsTokens.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuotaScenarioSupportStatus { Bounded, Unbounded, Infeasible, Unsupported, NumericallyUncertain }

/// <summary>Explicit sensitivity assumptions, in quota percentage points per interval. Null external upper bound means unrestricted external activity.</summary>
public sealed record QuotaCostSupportAssumptions(double? ExternalActivityUpperBound, double LagAllowance, double ModelMismatchAllowance);

/// <summary>Conditional local-work identification range, never a predictive interval or provider tariff.</summary>
public sealed record QuotaScenarioSupportResult(
    string Version,
    QuotaScenarioSupportStatus Status,
    double? LowerCost,
    double? UpperCost,
    int Constraints,
    int ActiveVariables,
    int VertexSystems,
    int FeasibleVertices,
    IReadOnlyList<string> UnobservedRequestedCoordinates,
    IReadOnlyList<string> MeasurementPolicies,
    QuotaCostSupportAssumptions Assumptions,
    string Reason,
    string Methodology)
{
    public QuotaUncertaintyKind RangeKind => QuotaUncertaintyKind.ConditionalIdentification;
}

public sealed record QuotaCostScenarioDiagnostic(QuotaHistoryCohort Cohort, double HorizonHours,
    string Scenario, DateTimeOffset ScenarioObservedAtUtc, string AssumptionCase, QuotaScenarioSupportResult Result);
