using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.App.Pages;

public sealed partial class CodexIntelligencePage
{
    private readonly PlannerRevision _planRevision = new();
    private CancellationTokenSource? _planRequest;
    private QuotaSnapshot[] _planAnchors = [];

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (Equals(e.Parameter, "planning")) Views.SelectedIndex = 2;
        else Views.SelectedIndex = 0;
    }

    private QuotaSnapshot[] PlanAnchors() => App.Services.CodexIntelligence.Current.Current
        .Where(x => SelectedAccountKey is null || x.Current.AccountKey == SelectedAccountKey)
        .OrderBy(x => x.Current.Kind).Select(x => x.Current).ToArray();

    private void RefreshPlanAnchors()
    {
        QuotaSnapshot[] anchors = PlanAnchors();
        if (!_planAnchors.SequenceEqual(anchors))
        {
            _planAnchors = anchors;
            InvalidatePlan();
        }
    }

    private void InvalidatePlan()
    {
        _planRevision.Invalidate();
        _planRequest?.Cancel();
        if (_loaded) ScenarioText.Text = "Session or quota changed · estimate again for the current inputs.";
    }

    private void OnPlanNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loaded) InvalidatePlan();
    }

    private void OnPlanTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loaded) InvalidatePlan();
    }

    private async void OnRecentPattern(object sender, RoutedEventArgs e)
    {
        long revision = _planRevision.Value;
        RecentPatternButton.IsEnabled = false;
        using var request = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _planRequest?.Cancel();
        _planRequest = request;
        try
        {
            RecentScenarioPattern pattern = await App.Services.CodexIntelligence.GetRecentScenarioPatternAsync(DateTimeOffset.UtcNow, request.Token);
            if (!_loaded || !_planRevision.IsCurrent(revision) || request.IsCancellationRequested) return;
            RecentPatternText.Text = pattern.Explanation;
            if (!pattern.Available) return;
            if (pattern.RootSessions > PlanRoots.Maximum || pattern.SubagentSessions > PlanChildren.Maximum)
            {
                RecentPatternText.Text = "Recent counts exceed the input range; no values were silently clamped.";
                return;
            }
            PlanRoots.Value = pattern.RootSessions;
            PlanChildren.Value = pattern.SubagentSessions;
            PlanModel.Text = pattern.Model ?? "";
            PlanEffort.Text = pattern.ReasoningEffort ?? "";
            ScenarioText.Text = "Review the recent pattern, choose a duration, then estimate session fit.";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_loaded && _planRevision.IsCurrent(revision)) RecentPatternText.Text = "Recent pattern unavailable: " + error.GetBaseException().Message;
        }
        finally
        {
            if (_planRequest == request) _planRequest = null;
            RecentPatternButton.IsEnabled = true;
        }
    }

    private async void OnSimulate(object sender, RoutedEventArgs e)
    {
        RefreshPlanAnchors();
        InvalidatePlan();
        long revision = _planRevision.Value;
        double hours = PlanHours.Value, roots = PlanRoots.Value, children = PlanChildren.Value, days = PlanHistoryDays.Value;
        if (!double.IsFinite(hours) || hours <= 0 || hours > 168 || !double.IsFinite(roots) || roots < 0 || roots > 32 || roots != Math.Truncate(roots) ||
            !double.IsFinite(children) || children < 0 || children > 64 || children != Math.Truncate(children) || roots + children <= 0 ||
            !double.IsFinite(days) || days < 1 || days > 30)
        {
            ScenarioText.Text = "Enter a positive duration, whole session counts and 1–30 history days.";
            return;
        }
        CodexIntelligenceSnapshot live = App.Services.CodexIntelligence.Current;
        CurrentQuotaForecast[] readings = live.Current.Where(x => SelectedAccountKey is null || x.Current.AccountKey == SelectedAccountKey).ToArray();
        string?[] accounts = readings.Where(x => x.IsFresh && live.CurrentState.IsQuotaSnapshotFresh(x.Current))
            .Select(x => x.Current.AccountKey).Distinct().ToArray();
        if (accounts.Length != 1 || accounts[0] is null)
        {
            ScenarioText.Text = "A fresh provider reading with one known account is required. Saved history does not establish the current account.";
            return;
        }
        string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var scenario = new ScenarioRequest(hours, (int)roots, (int)children, Model: Optional(PlanModel.Text),
            ReasoningEffort: Optional(PlanEffort.Text), AccountKey: accounts[0]);
        using var request = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _planRequest = request;
        ScenarioText.Text = "Estimating this session against both allowances…";
        try
        {
            ScenarioEstimate result = await App.Services.CodexIntelligence.SimulateAsync(scenario, DateTimeOffset.UtcNow.AddDays(-days), request.Token);
            if (!_loaded || !_planRevision.IsCurrent(revision) || request.IsCancellationRequested || !_planAnchors.SequenceEqual(PlanAnchors())) return;
            ScenarioText.Text = string.Join("\n\n", new[] { result.FiveHour, result.Weekly }.Select(window =>
            {
                SessionPlanWindow fit = live.CurrentState.QuotaLanes.Any(x => x.Kind == window.Kind && x.NotReportedByProvider)
                    ? new SessionPlanWindow(SessionFit.Unknown, "Not reported", "Codex did not report this allowance. The other window is assessed independently.")
                    : SessionPlanProjection.Build(window, readings.FirstOrDefault(x => x.Current.Kind == window.Kind), hours, DateTimeOffset.UtcNow);
                return $"{QuotaOutlookProjection.Name(window.Kind)} · {fit.Summary}\n{fit.Detail}";
            })) + $"\n\nEvaluated {result.EvaluatedAtUtc?.ToLocalTime():g} · {QuotaAccountScope.Describe(scenario.AccountKey)}\n" + result.Methodology;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_loaded && _planRevision.IsCurrent(revision)) ScenarioText.Text = "Session estimate unavailable: " + error.GetBaseException().Message;
        }
        finally
        {
            if (_planRequest == request) _planRequest = null;
        }
    }

    private void OnQuotaHistory(object sender, RoutedEventArgs e) => App.Navigate(typeof(AnalyticsPage));
    private void OnForecastHistory(object sender, RoutedEventArgs e) => App.Navigate(typeof(ForecastsPage));
    private void OnModelLab(object sender, RoutedEventArgs e) => App.Navigate(typeof(ForecastsPage), "model-lab");
}
