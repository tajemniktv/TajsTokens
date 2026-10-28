namespace TajsTokens.App.Pages;

public sealed partial class AnalyticsPage : IDashboardPresentation
{
    private Microsoft.UI.Xaml.FrameworkElement[] Inputs => [RangeCombo, ShowActivity, BurnTabs, SourceFilter,
        BurnWindowFilter, SourceDetails, ActivityDetails, ResetSourceFilter, ResetKindFilter];
    private sealed record InputState(DashboardInput[] Values, string? IntervalId, object? SeriesKey);

    object IDashboardPresentation.CaptureInputs() => new InputState(DashboardInputs.Capture(Inputs),
        (BurnIntervalList.SelectedItem as BurnIntervalRow)?.IntervalId ?? _suspendedIntervalId,
        (TimelineSeries.SelectedItem as TimelineGroup)?.Key ?? _suspendedSeriesKey);

    void IDashboardPresentation.RestoreInputs(object state)
    {
        var saved = (InputState)state;
        DashboardInputs.Restore(saved.Values, Inputs);
        _suspendedIntervalId = saved.IntervalId;
        _suspendedSeriesKey = saved.SeriesKey;
    }
}
