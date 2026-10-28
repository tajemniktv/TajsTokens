using TajsTokens.Infrastructure.Services;

namespace TajsTokens.App.Pages;

public sealed partial class CodexStateDbExplorerPage : IDashboardPresentation
{
    private string? _restoredComparisonPath;
    private (string Table, int Page)? _restoredRawPage;
    private Microsoft.UI.Xaml.FrameworkElement[] Inputs => [TraceColumnTextBox, TraceValueTextBox];
    private sealed record InputState(DashboardInput[] Values, string? Database, string? Comparison, string? Table, int Page);

    object IDashboardPresentation.CaptureInputs() => new InputState(DashboardInputs.Capture(Inputs),
        _selectedDatabasePath, (ComparisonComboBox.SelectedItem as CodexStateDatabaseCandidate)?.Path,
        (TableList.SelectedItem as CodexStateTableInfo)?.Name ?? _suspendedTableName, _pageIndex);

    void IDashboardPresentation.RestoreInputs(object state)
    {
        var saved = (InputState)state;
        DashboardInputs.Restore(saved.Values, Inputs);
        _selectedDatabasePath = saved.Database;
        _restoredComparisonPath = saved.Comparison;
        _suspendedTableName = saved.Table;
        _restoredRawPage = saved.Table is { } table ? (table, saved.Page) : null;
    }
}
