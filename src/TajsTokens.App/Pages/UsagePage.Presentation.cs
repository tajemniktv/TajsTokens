namespace TajsTokens.App.Pages;

public sealed partial class UsagePage : IDashboardPresentation
{
    private (string Label, DateTimeOffset? Start, DateTimeOffset? End, string? Thread)? _restoredRow;
    private Microsoft.UI.Xaml.FrameworkElement[] Inputs => [RangeCombo, ViewCombo, SortCombo, DetailedColumns, SearchBox];
    private sealed record InputState(DashboardInput[] Values, DateTimeOffset? From, DateTimeOffset? To,
        string? Model, string? Repository, string? Session, string? Thread,
        (string Label, DateTimeOffset? Start, DateTimeOffset? End, string? Thread)? Row);

    object IDashboardPresentation.CaptureInputs() => new InputState(DashboardInputs.Capture(Inputs),
        _from, _to, _model, _repository, _session, _thread,
        RowsList.SelectedItem is UsageRow row ? (row.Label, row.Start, row.End, row.ThreadId) : _restoredRow);

    void IDashboardPresentation.RestoreInputs(object state)
    {
        var saved = (InputState)state;
        DashboardInputs.Restore(saved.Values, Inputs);
        (_from, _to, _model, _repository, _session, _thread) =
            (saved.From, saved.To, saved.Model, saved.Repository, saved.Session, saved.Thread);
        _restoredRow = saved.Row;
    }
}
