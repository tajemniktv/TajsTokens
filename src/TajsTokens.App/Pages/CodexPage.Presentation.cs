namespace TajsTokens.App.Pages;

public sealed partial class CodexPage : IDashboardPresentation
{
    private Microsoft.UI.Xaml.FrameworkElement[] Inputs => [RangeCombo, BucketCombo, ArchivedCheckBox, SearchTextBox];
    private sealed record InputState(DashboardInput[] Values, string? Thread, string? Group, bool ExecutionDetails,
        bool Usage, bool Fleet, object? UsageInputs);

    object IDashboardPresentation.CaptureInputs() => new InputState(DashboardInputs.Capture(Inputs),
        _selectedThreadId ?? _requestedThread, _selectedGroupKey, _showExecutionDetails,
        UsageViewButton.IsChecked == true, FleetViewButton.IsChecked == true,
        (_usagePage as IDashboardPresentation)?.CaptureInputs());

    void IDashboardPresentation.RestoreInputs(object state)
    {
        var saved = (InputState)state;
        DashboardInputs.Restore(saved.Values, Inputs);
        _selectedThreadId = _requestedThread ?? saved.Thread;
        _selectedGroupKey = saved.Group;
        _showExecutionDetails = saved.ExecutionDetails;
        if (saved.UsageInputs is not null)
        {
            ShowUsage(true);
            ((IDashboardPresentation)_usagePage!).RestoreInputs(saved.UsageInputs);
        }
        if (saved.Fleet) ShowFleet(true);
        else ShowUsage(saved.Usage);
    }
}
