using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Models;

namespace TajsTokens.App.Pages;

// Presentation only: every card is a projection of the same intelligence snapshot.
public sealed partial class CodexIntelligencePage
{
    private sealed record QuotaTile(string Title, string Remaining, double Value, string Reset, string Pace, string Freshness);

    private static string Compact(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1e9:0.##}B",
        >= 1_000_000 => $"{value / 1e6:0.##}M",
        >= 1_000 => $"{value / 1e3:0.##}K",
        _ => value.ToString("N0")
    };

    private void RenderDashboard()
    {
        if (_snapshot is not { } snapshot) return;
        var filters = new[] { snapshot.Selection.Model,
            snapshot.Selection.Project is { } project ? System.IO.Path.GetFileName(project) : null,
            snapshot.Selection.ThreadId is { } chat ? "Chat " + chat[..Math.Min(8, chat.Length)] : null,
            snapshot.Selection.AccountKey is not null ? "Selected account" : null }.Where(x => x is not null).ToArray();
        SelectionExpander.Header = filters.Length == 0 ? "History range · all local work" :
            "History filters · " + string.Join(" · ", filters);
        QuotaEmpty.Visibility = snapshot.Current.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QuotaEmpty.Text = "Current account quota is unavailable for this selection. Clear work filters or choose a range including today.";
        QuotaTiles.ItemsSource = snapshot.Current.Select(x =>
        {
            var pace = QuotaEvenBurn.FromSnapshot(x.Current);
            return new QuotaTile(x.Current.Kind == Core.Enums.QuotaWindowKind.Weekly ? "Weekly allowance" : "5-hour allowance",
                x.Current.RemainingPercent is { } left ? $"{left:0.#}% left" : "Not reported",
                x.Current.RemainingPercent ?? 0,
                x.Current.ResetsAtUtc is { } reset ? $"Resets {reset.ToLocalTime():ddd, d MMM · HH:mm}" : "Reset time not reported",
                pace is null ? "Even-burn comparison unavailable" :
                    $"{Math.Abs(pace.ExcessPercentagePoints):0.#} pp {(pace.ExcessPercentagePoints > 0 ? "ahead of" : "below")} even burn · {pace.ElapsedPercent:0.#}% elapsed (inferred window)",
                $"{(x.IsFresh ? "Updated" : x.State + " · last reading")} {x.Current.CapturedAtUtc.ToLocalTime():HH:mm} · reported quota");
        }).ToArray();

        var forecast = snapshot.Current.FirstOrDefault(x => x.IsFresh && x.Forecast is not null);
        var horizon = forecast?.Forecast?.Evidence?.HorizonPredictions?.OrderBy(x => x.HorizonHours).FirstOrDefault();
        OutlookValue.Text = horizon is not null ? $"~{horizon.ExpectedUsagePercent:0.#} pp in {horizon.HorizonHours * 60:g} min"
            : forecast?.Forecast?.ProjectedRemainingAtResetPercent is { } left ? $"~{left:0.#}% left at reset" : "Learning your pace";
        OutlookDetail.Text = forecast is null ? "No fresh, compatible quota forecast yet. Observed quota remains available independently."
            : $"{forecast.Current.Kind} allowance · if recent conditions continue. " +
                (horizon?.LowerRemainingPercent is { } low && horizon.UpperRemainingPercent is { } high
                    ? $"Empirical range: {low:0.#}–{high:0.#}% remaining at that horizon."
                    : "Uncertainty is still learning; this is not a guarantee.");
        var work = snapshot.Workload;
        var prediction = work?.Predictions.OrderBy(x => Math.Abs(x.HorizonHours - .25)).FirstOrDefault();
        NowcastSummary.Text = work?.IsStale == true ? "Local workload telemetry is stale; no active nowcast."
            : prediction is null ? "Local workload nowcast is not available for this scope."
            : $"Local workload · ~{Compact((long)Math.Clamp(prediction.ExpectedTokens, 0, long.MaxValue))} tokens over the next {prediction.HorizonHours * 60:g} minutes. Separate from quota cost.";

        WorkValue.Text = Compact(snapshot.Ledger.Sum(x => x.Workload.ReportedTotalTokens)) + " tokens";
        WorkRange.Text = $"Selected history · {snapshot.Selection.FromUtc:dd MMM}–{snapshot.Selection.ToUtc.AddTicks(-1):dd MMM yyyy} UTC";
        var leading = snapshot.Breakdown.Where(x => x.Dimension == "Model").MaxBy(x => x.Tokens);
        WorkDetail.Text = $"{snapshot.Ledger.Select(x => x.ThreadId).Distinct().Count():N0} chats · " +
            (leading is null ? "No recorded work in this selection." : $"most work on {leading.Key}");
    }

    private void OnDashboardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 780;
        OutlookColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(OutlookCard, narrow ? 0 : 1);
        Grid.SetRow(OutlookCard, narrow ? 1 : 0);
    }

    private void OnViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded && Views.SelectedIndex == 0) RenderTimeline();
    }
    private void OnHistory(object sender, RoutedEventArgs e) => Views.SelectedIndex = 1;
    private void OnPlan(object sender, RoutedEventArgs e) => Views.SelectedIndex = 2;
    private void OnEvidence(object sender, RoutedEventArgs e)
    {
        Views.SelectedIndex = 3;
        QuotaEvidence.IsExpanded = true;
    }
    private async void OnQuickRange(object sender, RoutedEventArgs e)
    {
        var days = int.Parse((string)((Button)sender).Tag);
        FromDate.Date = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(1 - days);
        ToDate.Date = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(1);
        await LoadAsync();
    }
    private async void OnClearFilters(object sender, RoutedEventArgs e)
    {
        Model.Text = Project.Text = Thread.Text = "";
        await LoadAsync();
    }
}
