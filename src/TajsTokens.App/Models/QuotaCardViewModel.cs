// Taj's Tokens | QuotaCardViewModel.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using Microsoft.UI.Xaml.Controls;

#endregion

namespace TajsTokens.App.Models;

public sealed record QuotaCardViewModel(
    string Title,
    string RemainingPercent,
    string ResetCountdown,
    string BurnRate,
    string PredictedExhaustion,
    string GaugeText,
    string SurvivalMessage,
    InfoBarSeverity Severity)
{
    public double RemainingValue { get; init; }
    public string ResetTimestamp { get; init; } = "Reset time unavailable";
    public string Status { get; init; } = "Connecting";
    public bool NeedsAttention { get; init; }
    public bool IsReported { get; init; } = true;
    public string EvenBurn { get; init; } = "Even burn unavailable";
}