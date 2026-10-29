using System.Security.Cryptography;
using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Captures existing outputs and a fixed direct comparator; never selects a live model.</summary>
public static class QuotaForecastComputationBuilder
{
    public static QuotaForecastComputation? Build(QuotaSnapshot anchor, Forecast forecast,
        IReadOnlyList<QuotaSnapshot> inputs, QuotaForecastComputationPhase phase)
    {
        if (anchor.Authority != QuotaObservationAuthority.ProviderAuthoritative ||
            string.IsNullOrWhiteSpace(anchor.AccountKey) || anchor.ResetsAtUtc is not { } reset ||
            reset <= anchor.CapturedAtUtc || forecast.Kind != anchor.Kind) return null;
        var points = new List<QuotaForecastComputedPoint>();
        ForecastEvidence? evidence = forecast.Evidence;
        points.Add(new(QuotaForecastPointRole.SelectedReset, reset,
            (reset - anchor.CapturedAtUtc).TotalHours, evidence?.Model ?? "not-estimated",
            forecast.ProjectedRemainingAtResetPercent, null, evidence?.RemainingAtResetLowerPercent,
            evidence?.RemainingAtResetUpperPercent, 0, evidence?.CalibrationEpochs ?? 0,
            evidence?.CalibrationEpochs ?? 0, false, evidence?.SupportReasons ?? ForecastSupportReason.InsufficientHistory,
            forecast.EstimatedExhaustionAtUtc, forecast.SurvivesUntilReset));
        foreach (QuotaHorizonPrediction prediction in evidence?.HorizonPredictions ?? [])
            points.Add(new(QuotaForecastPointRole.SelectedHorizon, prediction.TargetUtc, prediction.HorizonHours,
                prediction.Model, prediction.RemainingPercent, prediction.ExpectedUsagePercent,
                prediction.LowerRemainingPercent, prediction.UpperRemainingPercent, prediction.TrainingSamples,
                prediction.ValidationSamples, prediction.IntervalSamples, prediction.UsesWorkload,
                prediction.SupportReasons, Inference: prediction.Inference));

        // Reuse the frozen direct fallback on the exact compatible epoch. These shadow outputs
        // provide 5/15-minute availability without changing the released 30/120-minute selector.
        QuotaSnapshot[] stream = QuotaHistoryPolicy.PaceRows(inputs, QuotaHistoryPolicy.Cohort(anchor));
        IReadOnlyList<QuotaSnapshot>? epoch = QuotaForecastCalibration.SplitEpochs(stream).LastOrDefault();
        bool usable = epoch is { Count: >= 2 } && epoch[^1] == anchor &&
            anchor.CapturedAtUtc - epoch[0].CapturedAtUtc >= TimeSpan.FromMinutes(15) &&
            anchor.UsedPercent is >= 0 and < 100;
        bool flat = usable && epoch!.All(row => row.UsedPercent == anchor.UsedPercent);
        foreach (double minutes in new[] { 5d, 15d, 30d, 120d })
        {
            double horizon = minutes / 60;
            DateTimeOffset target = anchor.CapturedAtUtc.AddMinutes(minutes);
            bool supported = usable && target <= reset;
            double? remaining = supported ? QuotaPaceModels.ProjectRemaining(epoch!, QuotaPaceModels.ProductionFallback, horizon) : null;
            points.Add(new(QuotaForecastPointRole.DirectBaseline, target, horizon, QuotaPaceModels.ProductionFallback,
                remaining, remaining is { } value ? anchor.RemainingPercent - value : null,
                null, null, 0, 0, 0, false,
                !usable ? ForecastSupportReason.InsufficientHistory : target > reset ? ForecastSupportReason.MissingOrExpiredReset :
                flat ? ForecastSupportReason.FlatMeter : ForecastSupportReason.None));
        }
        string hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(inputs)));
        return new(phase, QuotaHistoryPolicy.Cohort(anchor), anchor.CapturedAtUtc,
            anchor.HasSourceTimestamp ? anchor.ReplayMeasuredAtUtc ?? anchor.CapturedAtUtc : null,
            anchor.CollectedAtUtc, anchor.ObservationId, anchor.UsedPercent, reset, forecast.GeneratedAtUtc,
            evidence?.PolicyVersion ?? QuotaPredictionService.ResetOutlookPolicyVersion, hash, evidence?.PaceReceipt, points);
    }
}
