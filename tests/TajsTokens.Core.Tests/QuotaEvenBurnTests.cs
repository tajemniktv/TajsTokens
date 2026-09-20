// Taj's Tokens | QuotaEvenBurnTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class QuotaEvenBurnTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);

    private static QuotaSnapshot Point => new(
        QuotaWindowKind.FiveHour,
        Start.AddHours(2),
        62,
        300,
        Start.AddHours(5),
        "codex",
        "default",
        "app-server");

    [Fact]
    public void ComparesAtCaptureTimeWithoutForecastOrHistory()
    {
        var result = Assert.IsType<QuotaEvenBurn>(QuotaEvenBurn.FromSnapshot(Point));
        Assert.Equal(40, result.ElapsedPercent, 8);
        Assert.Equal(22, result.ExcessPercentagePoints, 8);
        var changedDuration = Assert.IsType<QuotaEvenBurn>(QuotaEvenBurn.FromSnapshot(Point with { WindowMinutes = 180 }));
        Assert.Equal(0, changedDuration.ElapsedPercent);
    }

    [Fact]
    public void MissingInvalidAndOutsideWindowEvidenceDoesNotInventPace()
    {
        QuotaSnapshot[] invalid =
        [
            Point with { WindowMinutes = null }, Point with { WindowMinutes = 0 },
            Point with { ResetsAtUtc = null }, Point with { UsedPercent = null },
            Point with { UsedPercent = double.NaN }, Point with { UsedPercent = double.PositiveInfinity },
            Point with { UsedPercent = -1 }, Point with { UsedPercent = 101 },
            Point with { CapturedAtUtc = Start.AddSeconds(-1) },
            Point with { CapturedAtUtc = Start.AddHours(5) }, Point with { CapturedAtUtc = Start.AddHours(6) },
        ];
        foreach (QuotaSnapshot point in invalid) Assert.Null(QuotaEvenBurn.FromSnapshot(point));
    }
}