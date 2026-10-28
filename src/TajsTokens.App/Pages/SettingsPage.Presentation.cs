using TajsTokens.Core.Models;

namespace TajsTokens.App.Pages;

public sealed partial class SettingsPage : IDashboardPresentation
{
    private bool _inputsInitialized;
    private Microsoft.UI.Xaml.FrameworkElement[] Inputs => [RunInBackgroundToggle, LaunchAtLoginToggle,
        LocalLimitsApiToggle, LocalLimitsApiPortBox, PollIntervalBox, NotificationsToggle, ThresholdsTextBox,
        RemoteFleetHostsBox, TokscaleReconciliationToggle, TokscaleFallbackToggle, CodexCliCommandBox,
        CodexCliArgumentsBox, CodexCliWorkingDirectoryBox, CodexCliPromptModeCombo, CodexCliTimeoutBox];
    private sealed record InputState(DashboardInput[] Values, RuntimeSettings? Baseline);

    object IDashboardPresentation.CaptureInputs() => new InputState(DashboardInputs.Capture(Inputs), _renderedSettings);

    void IDashboardPresentation.RestoreInputs(object state)
    {
        var saved = (InputState)state;
        RenderSettings();
        DashboardInputs.Restore(saved.Values, Inputs);
        // Preserve optimistic-save conflict detection against the form's original settings.
        _renderedSettings = saved.Baseline;
        _inputsInitialized = true;
    }
}
