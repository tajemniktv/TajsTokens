namespace TajsTokens.App.Pages;

public sealed partial class CodexDataExplorerPage : IDashboardPresentation
{
    private (Type Type, object? Inputs)? _restoredExplorer;
    private sealed record InputState(Type? Type, object? Inputs, string? Thread);

    object IDashboardPresentation.CaptureInputs() => new InputState(ExplorerFrame.Content?.GetType(),
        (ExplorerFrame.Content as IDashboardPresentation)?.CaptureInputs(), _threadId);

    void IDashboardPresentation.RestoreInputs(object state)
    {
        var saved = (InputState)state;
        if (saved.Type is not null) _restoredExplorer = (saved.Type, saved.Thread == _threadId ? saved.Inputs : null);
    }
}
