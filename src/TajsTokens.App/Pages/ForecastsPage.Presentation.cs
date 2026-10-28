using TajsTokens.Core.Enums;

namespace TajsTokens.App.Pages;

public sealed partial class ForecastsPage : IDashboardPresentation
{
    private string? _restoredEvaluationGroup;
    private Microsoft.UI.Xaml.FrameworkElement[] Inputs => [HistoryRangeCombo, HistorySummary, WindowFilter,
        HistoryDensity, ForecastTabs];
    private sealed record InputState(DashboardInput[] Values, int NormalTab, bool ModelLab, string? EvaluationGroup,
        (string Provider, string Profile, QuotaWindowKind Kind, DateTimeOffset GeneratedAtUtc, string? AccountKey)? Forecast);

    object IDashboardPresentation.CaptureInputs() => new InputState(DashboardInputs.Capture(Inputs),
        ReferenceEquals(_normalTab, UsageTab) ? 1 : ReferenceEquals(_normalTab, HistoryTab) ? 2 : 0,
        _modelLab,
        EvaluationGroup.SelectedItem as string ?? _restoredEvaluationGroup,
        (ForecastList.SelectedItem as ForecastRow)?.Identity ?? _suspendedForecast);

    void IDashboardPresentation.RestoreInputs(object state)
    {
        var saved = (InputState)state;
        DashboardInputs.Restore(saved.Values, saved.ModelLab == _modelLab
            ? Inputs : Inputs.Where(input => !ReferenceEquals(input, ForecastTabs)).ToArray());
        _normalTab = saved.NormalTab == 1 ? UsageTab : saved.NormalTab == 2 ? HistoryTab : OutlookTab;
        if (saved.ModelLab != _modelLab) ForecastTabs.SelectedItem = _modelLab ? EvaluationTab : _normalTab;
        _suspendedForecast = saved.Forecast;
        _restoredEvaluationGroup = saved.EvaluationGroup;
    }
}
