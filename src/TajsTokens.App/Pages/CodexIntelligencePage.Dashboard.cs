// Taj's Tokens | CodexIntelligencePage.Dashboard.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.App.Pages;

// Presentation only: every card is a projection of the same intelligence snapshot.
public sealed partial class CodexIntelligencePage
{
    private IReadOnlyList<CodexLedgerEntry>? _workLedger;

    private static string Compact(long value)
    {
        return value switch
        {
            >= 1_000_000_000 => $"{value / 1e9:0.##}B",
            >= 1_000_000 => $"{value / 1e6:0.##}M",
            >= 1_000 => $"{value / 1e3:0.##}K",
            _ => value.ToString("N0"),
        };
    }

    private void RenderDashboard()
    {
        var current = App.Services.CodexIntelligence.Current;
        CodexIntelligenceSnapshot live = CodexIntelligenceProjection.WithLiveContext(current, current, SelectedAccountKey);
        var product = CodexProductProjection.Build(CodexIntelligenceProjection.WithLiveContext(_snapshot ?? live, current, SelectedAccountKey), DateTimeOffset.UtcNow);
        AccountOutlook outlook = product.Allowances;
        QuotaEmpty.Visibility = live.Current.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QuotaEmpty.Text = "No current reading for this account. Selected work remains available below.";
        AccountScopeText.Text = SelectedAccountKey is null
            ? "Current provider account · separate from all local work"
            : "Selected account · only its current readings";
        double cardWidth = DashboardCards.ActualWidth < 780 ? DashboardCards.ActualWidth : DashboardCards.ActualWidth / 2;
        double tileWidth = Math.Max(120, (cardWidth - 64) / 2);
        QuotaTile[] tiles = outlook.Windows.Select(x => new QuotaTile(
            tileWidth,
            QuotaOutlookProjection.Name(x.Kind) + " allowance",
            x.Reading?.Current.UsedPercent is >= 0 and <= 100 && x.Reading.Current.RemainingPercent is { } left ? $"{left:0.#}% left" : x.NotReported ? "Not reported" : "Unavailable",
            x.Reading?.Current.RemainingPercent ?? 0,
            x.Reading?.Current.UsedPercent is >= 0 and <= 100 ? Visibility.Visible : Visibility.Collapsed,
            x.Reading?.Current.ResetsAtUtc is { } reset ? $"Resets {reset.ToLocalTime():ddd, d MMM · HH:mm}" : "Reset time unavailable",
            x.Conclusion,
            x.Reading is { } reading ? $"{(reading.IsFresh ? "Reported" : "Last known")} · {reading.Current.CapturedAtUtc.ToLocalTime():g} · {QuotaAccountScope.Describe(reading.Current.AccountKey)}" : x.Detail)).ToArray();
        if (QuotaTiles.ItemsSource is not QuotaTile[] previous || !previous.SequenceEqual(tiles)) QuotaTiles.ItemsSource = tiles;
        OutlookValue.Text = outlook.Summary;
        OutlookDetail.Text = string.Join("\n", outlook.Windows.Where(x => x.Reading is not null)
            .Select(x => $"{QuotaOutlookProjection.Name(x.Kind)} · {CodexProductProjection.PaceLabel(x)}"));
        StatusChangeSummary.Text = product.Changes.FirstOrDefault()?.Summary ?? "";
        ForecastText.Text = CodexProductProjection.Details(product);
        var marker = CodexRegimeMarkerProjection.Build(_snapshot?.Selection, _snapshot?.Accounting, _snapshot?.Regime, _snapshot?.Manifest.Id);
        RegimeMarkerLink.Content = marker.Summary;
        ToolTipService.SetToolTip(RegimeMarkerLink, marker.Detail);
        if (_snapshot is not { } snapshot) return;
        TtValue.Text = CodexProductProjection.Tt(product.Work.Tt);
        TtBasisLabel.Text = CodexProductProjection.WorkLabel(product.Work);
        PlanMeasuredWork.Text = product.Work.Scope + ": " + CodexProductProjection.Tokens(product.Work.NativeTokens) +
            " · " + CodexProductProjection.Tt(product.Work.Tt) + "\n" + CodexProductProjection.WorkLabel(product.Work);
        string?[] filters = new[]
        {
            snapshot.Selection.Model,
            snapshot.Selection.Project is { } project ? Path.GetFileName(project) : null,
            snapshot.Selection.ThreadId is { } chat ? "Chat " + chat[..Math.Min(8, chat.Length)] : null,
            snapshot.Selection.AccountKey is not null ? "Selected account" : null,
        }.Where(x => x is not null).ToArray();
        SelectionExpander.Header = filters.Length == 0 ? "History range · all local work" : "History filters · " + string.Join(" · ", filters);
        NowcastSummary.Text = CodexProductProjection.NowcastSummary(product);

        WorkRange.Text = product.Work.Scope;
        if (ReferenceEquals(_workLedger, snapshot.Ledger)) return;
        _workLedger = snapshot.Ledger;
        WorkValue.Text = CodexProductProjection.Tokens(product.Work.NativeTokens);
        CodexLedgerGroup? leading = snapshot.Breakdown.Where(x => x.Dimension == "Model").MaxBy(x => x.Tokens);
        WorkDetail.Text = $"{snapshot.Ledger.Select(x => x.ThreadId).Distinct().Count():N0} chats · " +
                          (leading is null ? "No recorded work in this selection." : $"most work on {leading.Key}");
    }

    private void OnDashboardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 780;
        OutlookColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(OutlookCard, narrow ? 0 : 1);
        Grid.SetRow(OutlookCard, narrow ? 1 : 0);
        if (_loaded && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > .5) RenderDashboard();
    }

    private void OnViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded && Views.SelectedIndex == 0) RenderTimeline();
    }

    private void OnHistory(object sender, RoutedEventArgs e)
    {
        Views.SelectedIndex = 1;
    }

    private void OnPlan(object sender, RoutedEventArgs e)
    {
        Views.SelectedIndex = 2;
    }

    private void OnEvidence(object sender, RoutedEventArgs e)
    {
        Views.SelectedIndex = 3;
        QuotaEvidence.IsExpanded = true;
    }

    private void OnAccountingResearch(object sender, RoutedEventArgs e)
    {
        Views.SelectedIndex = 3;
        AccountingExpander.IsExpanded = true;
        DispatcherQueue.TryEnqueue(() => { if (_loaded) AccountingExpander.StartBringIntoView(); });
    }

    private async void OnQuickRange(object sender, RoutedEventArgs e)
    {
        int days = int.Parse((string)((Button)sender).Tag);
        FromDate.Date = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(1 - days);
        ToDate.Date = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(1);
        await LoadAsync();
    }

    private async void OnClearFilters(object sender, RoutedEventArgs e)
    {
        Model.Text = Project.Text = Thread.Text = "";
        await LoadAsync();
    }

    private sealed record QuotaTile(double Width, string Title, string Remaining, double Value, Visibility MeterVisibility, string Reset, string Pace, string Freshness);
}
