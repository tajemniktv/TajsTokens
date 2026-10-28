namespace TajsTokens.App.Pages;

// Called only after the page has unloaded. Large read models and generated
// history visuals are rebuildable; IDashboardPresentation owns saved inputs.
internal interface IHistoryPresentation
{
    void ReleaseHistory();
}
