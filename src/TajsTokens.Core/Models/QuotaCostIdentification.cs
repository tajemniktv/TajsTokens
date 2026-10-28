namespace TajsTokens.Core.Models;

public sealed record QuotaDesignInformation(int Rows, int Columns, int ActiveColumns, int NumericalRank,
    double? ParticipationDimension, double? MaximumColumnCosine, IReadOnlyList<string> UnobservedColumns);

public sealed record QuotaScenarioDesignSupport(int ExaminedRows, int MissingOrInvalidJointRows, int ZeroWorkRows,
    int WithinTrainingSpan, int OutsideTrainingSpan, int NovelCoordinates, int OutsideTokenRange, int OutsideDurationRange);

public sealed record QuotaIdentificationCohort(QuotaHistoryCohort Cohort, double HorizonHours, int Observations,
    int ExaminedRows, int MissingJointRows, int InvalidJointRows, int ZeroWorkRows, int ZeroWorkPositiveMovementRows,
    int ZeroWorkPositiveEnvelopeRows, int LateOrUnknownAvailabilityRows,
    decimal ReportedTokens, decimal MissingModelTokens, decimal OverflowTokens,
    QuotaDesignInformation FrozenFirst20Joint, QuotaDesignInformation RetainedJoint,
    QuotaDesignInformation FrozenFirst20Marginals, QuotaScenarioDesignSupport LaterScenarios,
    TajsTokens.Core.Services.ChronologicalEvidence.Support ChronologicalSupport);

public sealed record QuotaCostIdentificationReport(string Version, string Methodology,
    IReadOnlyList<QuotaIdentificationCohort> Cohorts);
