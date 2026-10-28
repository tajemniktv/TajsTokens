namespace TajsTokens.App.Pages;

public sealed partial class CodexIntelligencePage : IDashboardPresentation
{
    private bool _planningNavigation;
    private bool _statusNavigation;
    private Microsoft.UI.Xaml.FrameworkElement[] Inputs => [FromDate, ToDate, Model, Project, Thread, Dimension,
        PlanHours, PlanModel, PlanEffort, PlanRoots, PlanChildren, PlanHistoryDays, PlanMode, PlanContext, PlanCounts, PlanDetails,
        Views, SelectionExpander, QuotaEvidence, CurrentStatusDetails,
        ReferenceExpander, PriceImportRole];
    private sealed record InputState(DashboardInput[] Values, string? AccountKey, bool Planning);

    object IDashboardPresentation.CaptureInputs() => new InputState(DashboardInputs.Capture(Inputs), SelectedAccountKey, _planningNavigation);

    void IDashboardPresentation.RestoreInputs(object state)
    {
        var saved = (InputState)state;
        DashboardInputs.Restore(saved.Values, Inputs);
        if (_statusNavigation) CurrentStatusDetails.IsExpanded = true;
        if (saved.Planning != _planningNavigation) Views.SelectedIndex = _planningNavigation ? 2 : 0;
        // Seed the identity before normal population, including an account no longer in live quota.
        Account.ItemsSource = new[] { new AccountOption(saved.AccountKey, "Retained account selection") };
        Account.SelectedIndex = 0;
        PopulateAccounts();
    }
}
