using TajsTokens.Core.Models;

namespace TajsTokens.App.Pages;

public sealed partial class CodexSourcesPage : IDashboardPresentation
{
    private string? _navigationThread;
    private Microsoft.UI.Xaml.FrameworkElement[] Inputs => [AuxiliaryThreadBox, FilterTextBox, LogLevelsTextBox,
        LogTargetTextBox, LogModuleTextBox, LogThreadTextBox, LogFromTextBox, LogToTextBox, LogProcessTextBox,
        LogIncludeThreadlessCheckBox, LogIncludeMessagesCheckBox];
    private sealed record InputState(DashboardInput[] Values, Dictionary<CodexNativeSourceKind, string> Paths,
        CodexAuxiliaryQuery Auxiliary, string? SourceKey, string? NavigationThread);

    object IDashboardPresentation.CaptureInputs() => new InputState(DashboardInputs.Capture(Inputs),
        new(_selectedPaths), _auxiliaryQuery, (SourceList.SelectedItem as SourceRow)?.Key ?? _suspendedSourceKey, _navigationThread);

    void IDashboardPresentation.RestoreInputs(object state)
    {
        var saved = (InputState)state;
        DashboardInputs.Restore(saved.Values, Inputs);
        foreach (var (kind, path) in saved.Paths) _selectedPaths[kind] = path;
        _auxiliaryQuery = saved.Auxiliary;
        _suspendedSourceKey = saved.SourceKey;
        // A new thread navigation wins; a restore retains filters edited since navigation.
        if (_navigationThread == saved.NavigationThread) _initialThreadId = null;
    }
}
