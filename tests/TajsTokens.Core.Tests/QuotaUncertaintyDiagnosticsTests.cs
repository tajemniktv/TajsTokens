using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaUncertaintyDiagnosticsTests
{
    private static QuotaCostTrial Trial(double p, double low, double high, double? bandLow, double? bandHigh) =>
        new(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), DateTimeOffset.UnixEpoch.AddDays(1),
            (low + high) / 2, low, high, p, Math.Max(0, Math.Max(low - p, p - high)), 0, bandLow, bandHigh);

    [Fact]
    public void IntersectionAndContainmentCannotBeConfusedWithLatentCoverage()
    {
        QuotaCostTrial[] trials = [Trial(10, 9, 11, 9.5, 10.5), Trial(10, 9, 11, 8, 12), Trial(10, 9, 11, 12, 14)];
        var result = QuotaUncertaintyDiagnosticsBuilder.Build(trials);
        Assert.Equal(3, result.RangeSamples);
        Assert.Equal(2d / 3, result.EnvelopeIntersectionRate!.Value, 8);
        Assert.Equal(1d / 3, result.EnvelopeContainmentRate!.Value, 8);
        Assert.Equal(7d / 3, result.MeanPredictiveRangeWidth!.Value, 8);
        // For every chosen latent point in its measurement set, empirical coverage
        // lies between containment and intersection on these same outcomes.
        foreach (double a in new[] { 9d, 10d, 11d }) foreach (double b in new[] { 9d, 10d, 11d })
        {
            double coverage = ((a >= 9.5 && a <= 10.5 ? 1 : 0) + (b >= 8 && b <= 12 ? 1 : 0)) / 3d;
            Assert.InRange(coverage, result.EnvelopeContainmentRate.Value, result.EnvelopeIntersectionRate.Value);
        }
        Assert.Equal(QuotaUncertaintyKind.MeasurementEnvelope, result.ObservationKind);
        Assert.Equal(QuotaUncertaintyKind.PredictiveResidualRange, result.ReportedRangeKind);
    }

    [Fact]
    public void PointErrorBoundsAreSharpForEachDeclaredMeasurementEnvelope()
    {
        var inside = QuotaUncertaintyDiagnosticsBuilder.Build([Trial(10, 9, 11, null, null)]);
        Assert.Equal(0, inside.MeanMinimumAbsolutePointError); Assert.Equal(1, inside.MeanMaximumAbsolutePointError);
        var outside = QuotaUncertaintyDiagnosticsBuilder.Build([Trial(5, 9, 11, null, null)]);
        Assert.Equal(4, outside.MeanMinimumAbsolutePointError); Assert.Equal(6, outside.MeanMaximumAbsolutePointError);
        Assert.Equal(2, outside.MeanMeasurementWidth);
        Assert.Equal(0, outside.RangeSamples); Assert.Null(outside.EnvelopeIntersectionRate);
    }

    [Fact]
    public void MissingAndInvalidRangesDoNotBecomeZeroWidthCertaintyOrFailures()
    {
        var result = QuotaUncertaintyDiagnosticsBuilder.Build([
            Trial(10, 9, 11, null, null), Trial(10, 9, 11, 11, 9), Trial(10, 9, 11, 9, null),
            Trial(double.NaN, 9, 11, 8, 12), Trial(10, 11, 9, 8, 12)]);
        Assert.Equal(3, result.PointSamples); Assert.Equal(2, result.InvalidPointSamples);
        Assert.Equal(0, result.RangeSamples); Assert.Equal(4, result.InvalidRangeSamples);
        Assert.Null(result.MeanPredictiveRangeWidth); Assert.Null(result.EnvelopeContainmentRate);
    }

    [Fact]
    public void ConditionalPathRangeKindSurvivesSerializationWithoutBecomingAResidualBand()
    {
        var result = QuotaUncertaintyDiagnosticsBuilder.Build([Trial(10, 9, 11, 8, 12)], QuotaUncertaintyKind.ConditionalResampledPathRange);
        var reopened = System.Text.Json.JsonSerializer.Deserialize<QuotaUncertaintyDiagnostics>(System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Equal(result, reopened);
        Assert.Equal(QuotaUncertaintyKind.ConditionalResampledPathRange, reopened!.ReportedRangeKind);
        Assert.Equal(QuotaUncertaintyKind.MeasurementEnvelope, reopened.ObservationKind);
    }
}
