// Taj's Tokens | QuotaCardPresenter.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using Microsoft.UI.Xaml.Controls;
using TajsTokens.App.Models;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.App.ViewModels;

internal static class QuotaCardPresenter
{
    internal static QuotaCardViewModel BuildQuotaCard(
        string title,
        QuotaSnapshot snapshot,
        Forecast? forecast,
        bool isFresh)
    {
        double? remaining = snapshot.RemainingPercent;
        QuotaEvenBurn? evenBurn = QuotaEvenBurn.FromSnapshot(snapshot);
        string evenBurnText = evenBurn is null
            ? "Even burn unavailable · requires a valid reset and duration"
            : $"Even burn at reading: {evenBurn.UsedPercent:0.#}% used · {evenBurn.ElapsedPercent:0.#}% elapsed\n" +
              $"{Math.Abs(evenBurn.ExcessPercentagePoints):0.#} pp {(evenBurn.ExcessPercentagePoints >= 0 ? "above" : "below")} even burn · not a forecast\n" +
              "Window start inferred from reported reset minus duration";
        string resetCountdown = snapshot.ResetsAtUtc is DateTimeOffset reset
            ? FormatTimeSpan(reset - DateTimeOffset.UtcNow)
            : "Unknown";

        if (!isFresh)
        {
            return new QuotaCardViewModel(
                title,
                remaining is double staleValue ? $"{staleValue:0.#}%" : "Unknown",
                resetCountdown,
                "Paused while stale",
                "Forecast paused",
                $"Last known good · {snapshot.Source} · {QuotaAccountScope.Describe(snapshot.AccountKey)}",
                "Last-known-good quota is shown; forecasting is paused until the provider is fresh again.",
                InfoBarSeverity.Warning)
            {
                Status = "Stale",
                NeedsAttention = true,
                RemainingValue = remaining ?? 0,
                EvenBurn = "Last-known reading · " + evenBurnText,
                ResetTimestamp = snapshot.ResetsAtUtc?.ToLocalTime().ToString("ddd d MMM HH:mm") ?? "Unknown reset",
            };
        }

        string paceText = forecast?.State switch
        {
            ForecastState.IdleWithinMeterPrecision => "Flat within meter precision",
            _ when forecast?.BurnRatePercentPerHour is double burn && forecast.SustainablePercentPerHour is double sustainable =>
                $"{burn:0.0} pp/h · sustainable {sustainable:0.0} pp/h" +
                (forecast.BurnPressure is double pressure ? $" · {pressure:0.00}× pace" : string.Empty),
            _ when forecast?.SustainablePercentPerHour is double sustainable => $"Sustainable {sustainable:0.0} pp/h · observed pace unavailable",
            _ => "Observed pace unavailable",
        };

        AllowanceOutlook outlook = QuotaOutlookProjection.Window(snapshot.Kind,
            new CurrentQuotaForecast(snapshot, TelemetryHealthState.Live, forecast, ""), false, DateTimeOffset.UtcNow);
        string windowForecast = outlook.Conclusion;
        string survivalMessage = outlook.Detail;
        InfoBarSeverity severity = outlook.Status is "At risk" or "Exhausted" ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;

        string confidence = forecast?.Evidence is { } evidence
            ? evidence.RemainingAtResetLowerPercent is double lower && evidence.RemainingAtResetUpperPercent is double upper
                ? $" · 80%-target band {lower:0}–{upper:0}% ({evidence.CalibrationEpochs} past resets)"
                : $" · reset range not yet supported ({evidence.CalibrationEpochs} comparable past resets)"
            : string.Empty;
        string methodology = forecast?.Evidence is { } diagnostics
            ? $"\n{diagnostics.UncertaintyDescription} Model: {diagnostics.Model}; {diagnostics.ObservationCount} observations over {diagnostics.ObservedHours:0.#}h."
            : string.Empty;
        string trend = string.IsNullOrWhiteSpace(forecast?.Trend) ? string.Empty : $" · {forecast.Trend}";
        if (forecast?.Evidence?.HorizonPredictions is { Count: > 0 } horizons)
        {
            methodology += "\n" + string.Join(
                "\n",
                horizons.Select(x =>
                    $"~{x.RemainingPercent:0.#}% left " +
                    $"at {x.TargetUtc.ToLocalTime():g} · {x.Model} · " +
                    (x.LowerRemainingPercent is { } low && x.UpperRemainingPercent is { } high
                        ? $"80%-target band {low:0.#}–{high:0.#}%"
                        : "range not yet supported") +
                    $" · {x.TrainingSamples} workload training / {x.ValidationSamples} validation outcomes"));
        }
        string freshness = $"live · {snapshot.Source} · {QuotaAccountScope.Describe(snapshot.AccountKey)}";
        if (snapshot.AccountKey is null)
            survivalMessage =
                "Quota is live, but backend-account scope was not reported. Forecasting will not borrow unknown-account history.";

        return new QuotaCardViewModel(
            title,
            remaining is double value ? $"{value:0.#}%" : "Unknown",
            resetCountdown,
            paceText,
            windowForecast,
            $"{freshness}{confidence}{trend}{methodology}",
            survivalMessage,
            severity)
        {
            RemainingValue = remaining ?? 0,
            EvenBurn = evenBurnText,
            ResetTimestamp = snapshot.ResetsAtUtc?.ToLocalTime().ToString("ddd d MMM HH:mm") ?? "Unknown reset",
            NeedsAttention = severity == InfoBarSeverity.Warning || snapshot.AccountKey is null,
            Status = outlook.Status,
        };
    }

    private static string FormatTimeSpan(TimeSpan timeSpan)
    {
        if (timeSpan <= TimeSpan.Zero)
        {
            return "due now";
        }

        if (timeSpan.TotalDays >= 1)
        {
            return $"{(int)timeSpan.TotalDays}d {timeSpan.Hours}h";
        }

        return $"{(int)timeSpan.TotalHours}h {timeSpan.Minutes}m";
    }
}