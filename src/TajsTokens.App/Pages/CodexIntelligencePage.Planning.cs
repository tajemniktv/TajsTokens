using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TajsTokens.Core.Enums;
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
        _planningNavigation = Equals(e.Parameter, "planning") || Equals(e.Parameter, "status");
        _statusNavigation = Equals(e.Parameter, "status");
        if (_planningNavigation) Views.SelectedIndex = 2;
        else Views.SelectedIndex = 0;
        if (_statusNavigation) CurrentStatusDetails.IsExpanded = true;
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
        if (_loaded)
        {
            ScenarioText.Text = "Session or quota changed · estimate again for the current inputs.";
            PlanDetailsText.Text = "Estimate again to inspect the current inputs and scope.";
        }
    }

    private void OnPlanSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        UpdatePlanMode();
        InvalidatePlan();
    }

    private void UpdatePlanMode()
    {
        bool recent = PlanMode.SelectedIndex == 0;
        PlanContext.IsEnabled = recent;
        RecentPatternText.Text = recent
            ? "Recent mode keeps the native mixture. Model/effort edits revalue the same tokens only."
            : "Count mode matches historical model/effort labels. Lookback and counts apply here.";
    }

    private void OnPlanNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loaded) InvalidatePlan();
    }

    private void OnPlanTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (_loaded) InvalidatePlan();
        if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) UpdateModelSuggestions(sender);
        UpdateModelDiscoveryHint();
    }

    private void UpdateModelSuggestions(AutoSuggestBox sender)
    {
        var choices = _snapshot?.ModelCatalog.Choices ?? [];
        if (ReferenceEquals(sender, PlanModel))
            sender.ItemsSource = choices.Where(x => x.ObservedInSelection || x.Metadata?.Visibility != "hide")
                .Select(x => x.Model).Where(x => x.Contains(sender.Text, StringComparison.OrdinalIgnoreCase)).Take(20).ToArray();
        else
        {
            var choice = choices.FirstOrDefault(x => x.Model == PlanModel.Text);
            sender.ItemsSource = (choice?.Metadata?.ReasoningEfforts ?? []).Concat(choice?.ObservedEfforts ?? [])
                .Distinct(StringComparer.Ordinal).Where(x => x.Contains(sender.Text, StringComparison.OrdinalIgnoreCase)).Take(20).ToArray();
        }
    }
    private void UpdateModelDiscoveryHint()
    {
        if (ModelDiscoveryText is null) return;
        var view = _snapshot?.ModelCatalog;
        var choice = view?.Choices.FirstOrDefault(x => x.Model == PlanModel.Text);
        ModelDiscoveryText.Text = choice?.Description ?? (string.IsNullOrWhiteSpace(PlanModel.Text)
            ? $"{view?.Choices.Count(x => x.ListedInRetainedCatalog) ?? 0} cached names · model access unverified"
            : "Unlisted name kept as entered · model access unverified");
    }

    private async void OnRecentPattern(object sender, RoutedEventArgs e)
    {
        if (PlanMode.SelectedIndex == 0)
        {
            PlanModel.Text = "";
            PlanEffort.Text = "";
            InvalidatePlan();
            RecentPatternText.Text = "Native mixture restored · estimate to continue the selected recent context.";
            return;
        }
        long revision = _planRevision.Value;
        RecentPatternButton.IsEnabled = false;
        using var request = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _planRequest?.Cancel();
        _planRequest = request;
        try
        {
            RecentScenarioPattern pattern = await App.Services.CodexIntelligence.GetRecentScenarioPatternAsync(DateTimeOffset.UtcNow, request.Token);
            if (!_loaded || !_planRevision.IsCurrent(revision) || request.IsCancellationRequested) return;
            if (!pattern.Available)
            {
                RecentPatternText.Text = "Recent counts unavailable · enter counts manually. See plan details.";
                PlanDetailsText.Text = pattern.Explanation;
                return;
            }
            if (pattern.RootSessions > PlanRoots.Maximum || pattern.SubagentSessions > PlanChildren.Maximum)
            {
                RecentPatternText.Text = "Recent counts exceed the input range; no values were silently clamped.";
                return;
            }
            PlanRoots.Value = pattern.RootSessions;
            PlanChildren.Value = pattern.SubagentSessions;
            PlanModel.Text = pattern.Model ?? "";
            PlanEffort.Text = pattern.ReasoningEffort ?? "";
            RecentPatternText.Text = "Filled recent token-active session counts · review before estimating.";
            PlanDetailsText.Text = pattern.Explanation;
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
        if (PlanMode.SelectedIndex == 0)
        {
            await EstimateRecentWorkAsync();
            return;
        }
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
            var fits = new[] { result.FiveHour, result.Weekly }.Select(window =>
            {
                SessionPlanWindow fit = live.CurrentState.QuotaLanes.Any(x => x.Kind == window.Kind && x.NotReportedByProvider)
                    ? new SessionPlanWindow(SessionFit.Unknown, "Not reported", "Codex did not report this allowance. The other window is assessed independently.")
                    : SessionPlanProjection.Build(window, readings.FirstOrDefault(x => x.Current.Kind == window.Kind), hours, DateTimeOffset.UtcNow);
                return (window.Kind, fit);
            }).ToArray();
            ScenarioText.Text = "Session-count comparator\n" + string.Join("\n", fits.Select(x => CompactFit(x.Kind, x.fit)));
            PlanDetailsText.Text = string.Join("\n\n", fits.Select(x => $"{QuotaOutlookProjection.Name(x.Kind)} · {x.fit.Detail}")) +
                $"\n\nEvaluated {result.EvaluatedAtUtc?.ToLocalTime():g} · {QuotaAccountScope.Describe(scenario.AccountKey)}\n" + result.Methodology;
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

    private async Task EstimateRecentWorkAsync()
    {
        long revision = _planRevision.Value;
        double hours = PlanHours.Value;
        if (!double.IsFinite(hours) || hours <= 0 || hours > 168)
        {
            ScenarioText.Text = "Enter a positive duration up to 168 hours.";
            return;
        }
        var plan = new RecentWorkPlanRequest(hours, PlanContext.SelectedIndex == 0 ? .5 : 2,
            PlanModel.Text, PlanEffort.Text, SelectedAccountKey);
        using var request = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _planRequest = request;
        ScenarioText.Text = "Estimating recent work and account pace…";
        try
        {
            var result = await App.Services.CodexIntelligence.PlanRecentWorkAsync(plan, request.Token);
            if (!_loaded || !_planRevision.IsCurrent(revision) || request.IsCancellationRequested || !_planAnchors.SequenceEqual(PlanAnchors())) return;
            var receipt = result.Continuation.Receipt;
            string native = receipt.ProjectedTokens is { } tokens ? $"~{tokens:N0} native tokens" : "Native work unavailable";
            ScenarioText.Text = $"If the last {plan.ContextHours * 60:0} minutes continue for {hours:0.##} h\n" + native +
                " · " + (result.Reference?.TtSummary ?? "TT unavailable") + "\n" +
                (result.HasEdits ? "Same-token edit: " + (result.Revaluation?.TtSummary ?? "revaluation unavailable") + " · task/quota edit unsupported\n" : "") +
                string.Join("\n", result.Windows.Select(x => CompactFit(x.Kind, x.Assessment))) +
                $"\nInstallation-local work · conditional · {result.EvaluatedAtUtc.ToLocalTime():g}" +
                (result.Reference?.Sources.Selection is { } selected ? $" · frozen {selected.Basis.Vintage}" : "");
            PlanDetailsText.Text = $"{RecentWorkPlan.Version}\nContext {receipt.ObservationFromUtc:O} → {receipt.ObservationToUtc:O}; " +
                $"{receipt.Observations:N0} available observations; {receipt.RecordedTokens:N0} recorded tokens. Status: {receipt.Status}.\n" +
                $"Latest local evidence {receipt.LatestEvidenceUtc?.ToLocalTime().ToString("g") ?? "unavailable"}; " +
                $"latest positive work {receipt.LatestPositiveWorkUtc?.ToLocalTime().ToString("g") ?? "unavailable"}.\n" +
                $"TT basis {result.Reference?.Sources.Selection?.Basis.BasisId ?? "unavailable"}.\n" + result.EditStatus + "\n" +
                (result.Reference?.PriceSummary ?? "API reference unavailable") +
                (result.HasEdits ? "\nSame-token edit · " + (result.Revaluation?.PriceSummary ?? "API reference unavailable") : "") +
                "\n\n" + string.Join("\n\n", result.Windows.Select(x =>
                    $"{QuotaOutlookProjection.Name(x.Kind)} · {QuotaAccountScope.Describe(x.Reading?.Current.AccountKey)}\n{x.Assessment.Detail}\n" +
                    (x.Pace.HasEnoughHistory ? x.Reading?.Forecast?.Evidence?.PaceReceipt?.Describe() : ""))) +
                "\n\n" + RecentWorkPlan.Methodology;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_loaded && _planRevision.IsCurrent(revision)) ScenarioText.Text = "Recent-work plan unavailable: " + error.GetBaseException().Message;
        }
        finally { if (_planRequest == request) _planRequest = null; }
    }

    private static string CompactFit(QuotaWindowKind kind, SessionPlanWindow fit)
    {
        string detail = fit.Detail.Split('\n')[0];
        int sentence = detail.IndexOf(". ", StringComparison.Ordinal);
        if (sentence >= 0) detail = detail[..(sentence + 1)];
        return $"{QuotaOutlookProjection.Name(kind)} · {fit.Summary} · {detail}";
    }

    private void OnQuotaHistory(object sender, RoutedEventArgs e) => App.Navigate(typeof(AnalyticsPage));
    private void OnForecastHistory(object sender, RoutedEventArgs e) => App.Navigate(typeof(ForecastsPage));
    private void OnModelLab(object sender, RoutedEventArgs e) => App.Navigate(typeof(ForecastsPage), "model-lab");
}
