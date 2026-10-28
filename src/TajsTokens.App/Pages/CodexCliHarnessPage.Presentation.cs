namespace TajsTokens.App.Pages;

public sealed partial class CodexCliHarnessPage : IDashboardPresentation
{
    private bool _inputsInitialized;
    private Microsoft.UI.Xaml.FrameworkElement[] Inputs => [CommandBox, ArgumentsBox, WorkingDirectoryBox,
        PromptModeCombo, TimeoutBox, PromptBox];

    object IDashboardPresentation.CaptureInputs() => DashboardInputs.Capture(Inputs);

    void IDashboardPresentation.RestoreInputs(object state)
    {
        DashboardInputs.Restore((DashboardInput[])state, Inputs);
        _inputsInitialized = true;
        StatusText.Text = "Inputs restored. Previous run output was released while hidden.";
    }
}
