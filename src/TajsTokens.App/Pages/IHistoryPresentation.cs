namespace TajsTokens.App.Pages;

// Called only after the page has unloaded. Controls keep lightweight user input;
// large read models and generated history visuals are rebuildable.
internal interface IHistoryPresentation
{
    void ReleaseHistory();
}
