using System.Text.Json.Serialization;

namespace TajsTokens.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuotaUncertaintyKind { MeasurementEnvelope, ConditionalIdentification, PredictiveResidualRange, ConditionalResampledPathRange }

/// <summary>Empirical diagnostics conditional on latent outcomes belonging to supplied measurement envelopes. No calibrated probability is implied.</summary>
public sealed record QuotaUncertaintyDiagnostics(
    int PointSamples,
    int InvalidPointSamples,
    int RangeSamples,
    int InvalidRangeSamples,
    double? MeanMeasurementWidth,
    double? MeanMinimumAbsolutePointError,
    double? MeanMaximumAbsolutePointError,
    double? MeanPredictiveRangeWidth,
    double? EnvelopeIntersectionRate,
    double? EnvelopeContainmentRate,
    string Methodology)
{
    public QuotaUncertaintyKind ObservationKind => QuotaUncertaintyKind.MeasurementEnvelope;
    public QuotaUncertaintyKind ReportedRangeKind { get; init; } = QuotaUncertaintyKind.PredictiveResidualRange;
}
