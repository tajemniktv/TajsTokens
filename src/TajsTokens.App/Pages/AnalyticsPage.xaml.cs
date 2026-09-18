using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Automation;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.App.Pages;

public sealed partial class AnalyticsPage : Page
{
    private readonly Dictionary<string, QuotaBurnInterval> _intervalsById = new(StringComparer.Ordinal);
    private CancellationTokenSource? _pageCancellation;
    private bool _isLoaded;
    private long _loadGeneration;
    private long _selectionGeneration;
    private IReadOnlyList<ResetRow> _resetRows = [];
    private string _burnStatus = "Loading quota changes…";
    private string? _loadError;

    public AnalyticsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, e) =>
        {
            var wide = e.NewSize.Width >= (double)Application.Current.Resources["WideContentBreakpoint"];
            BurnTabs.Height = wide ? 600 : 760;
            BurnDetailColumn.Width = wide ? new GridLength(1.6, GridUnitType.Star) : new GridLength(0);
            BurnDetailRow.Height = wide ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(BurnDetails, wide ? 1 : 0); Grid.SetRow(BurnDetails, wide ? 0 : 1);
            SummaryThirdColumn.Width = SummaryFourthColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(SourceSummary, wide ? 2 : 0); Grid.SetRow(SourceSummary, wide ? 0 : 1);
            Grid.SetColumn(ActivitySummary, wide ? 3 : 1); Grid.SetRow(ActivitySummary, wide ? 0 : 1);
        };
    }

    private App App => (App)Application.Current;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        var previous = Interlocked.Exchange(ref _pageCancellation, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
        var generation = Interlocked.Increment(ref _loadGeneration);
        await LoadAsync(_pageCancellation.Token, generation);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        Interlocked.Increment(ref _loadGeneration);
        Interlocked.Increment(ref _selectionGeneration);
        var cancellation = Interlocked.Exchange(ref _pageCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        var cancellation = _pageCancellation;
        if (!_isLoaded || cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }
        var generation = Interlocked.Increment(ref _loadGeneration);
        await LoadAsync(cancellation.Token, generation);
    }

    private async Task LoadAsync(CancellationToken cancellationToken, long generation)
    {
        if (!_isLoaded)
        {
            return;
        }

        try
        {
            StatusText.Text = "Correlating quota observations with normalized Codex activity…";
            var now = DateTimeOffset.UtcNow;
            var days = ParseDays();
            var authority = SourceFilter.SelectedIndex switch
            {
                0 => (QuotaObservationAuthority?)QuotaObservationAuthority.ProviderAuthoritative,
                1 => QuotaObservationAuthority.EmbeddedObservation,
                _ => null
            };
            var kind = BurnWindowFilter.SelectedIndex switch
            {
                1 => (QuotaWindowKind?)QuotaWindowKind.FiveHour,
                2 => QuotaWindowKind.Weekly,
                _ => null
            };
            var dashboard = await Task.Run(
                () => App.Services.Intelligence.QueryAsync(
                    new IntelligenceQuery(now.AddDays(-days), now, AnalyticsBucketSize.Hour, 720, authority, kind),
                    cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (!_isLoaded || generation != Volatile.Read(ref _loadGeneration))
            {
                return;
            }

            Render(dashboard);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_isLoaded && generation == Volatile.Read(ref _loadGeneration))
            {
                _loadError = $"Quota intelligence unavailable: {Summarize(exception.Message)} · displayed results may be stale.";
                StatusText.Text = _loadError;
            }
        }
    }

    private void Render(IntelligenceDashboard dashboard)
    {
        _loadError = null;
        var selectedId = (BurnIntervalList.SelectedItem as BurnIntervalRow)?.IntervalId;
        _intervalsById.Clear();
        foreach (var interval in dashboard.QuotaBurnIntervals)
        {
            _intervalsById[interval.IntervalId] = interval;
        }
        var previousSeries = (TimelineSeries.SelectedItem as TimelineGroup)?.Key;
        var series = dashboard.QuotaBurnIntervals.GroupBy(x => (x.Kind, x.AccountKey, x.BeforeSource, x.AfterSource, x.ResetsAtUtc))
            .OrderByDescending(g => g.Max(x => x.EndUtc)).Select((g, i) => new TimelineGroup(g.Key,
                $"{FormatKind(g.Key.Kind)} · {SourceLabel(g.Key.AfterSource)} · reset {g.Key.ResetsAtUtc?.ToLocalTime():d MMM HH:mm} · series {i + 1}",
                g.OrderBy(x => x.StartUtc).ToArray())).ToArray();
        TimelineSeries.ItemsSource = series;
        TimelineSeries.SelectedItem = series.FirstOrDefault(x => Equals(x.Key, previousSeries)) ?? series.FirstOrDefault();
        RenderTimeline();

        BurnIntervalCountText.Text = dashboard.QuotaBurnIntervals.Count.ToString("N0");
        LargestDropText.Text = dashboard.QuotaBurnIntervals.Count == 0
            ? "None observed"
            : $"+{dashboard.QuotaBurnIntervals.Max(interval => interval.DeltaUsedPercent):0.#} pp";
        ResetCountText.Text = SourceFilter.SelectedIndex switch { 0 => "Account meter", 1 => "Rollout readings", _ => "Separate sources" };
        var correlated = dashboard.QuotaBurnIntervals.Count(interval => interval.NativeTokens > 0);
        CorrelatedIntervalsText.Text = dashboard.QuotaBurnIntervals.Count == 0
            ? "—"
            : $"{correlated:N0} / {dashboard.QuotaBurnIntervals.Count:N0}";

        var burnRows = dashboard.QuotaBurnIntervals.Select(interval => new BurnIntervalRow(
            interval.IntervalId,
            $"{interval.StartUtc.ToLocalTime():dd MMM HH:mm:ss} → {interval.EndUtc.ToLocalTime():dd MMM HH:mm:ss}",
            $"{FormatKind(interval.Kind)} · {interval.BeforeUsedPercent:0.#}% → {interval.AfterUsedPercent:0.#}% used (+{interval.DeltaUsedPercent:0.#} pp)",
            SourceLabel(interval.AfterSource) +
            $" · {QuotaAccountScope.Describe(interval.AccountKey)}"))
            .ToArray();
        BurnIntervalList.ItemsSource = burnRows.Length == 0
            ? new[] { new BurnIntervalRow(string.Empty, "No changes in this view", "Try a longer range or another source. Missing readings do not mean zero consumption.") }
            : burnRows;

        if (!string.IsNullOrEmpty(selectedId))
        {
            BurnIntervalList.SelectedItem = burnRows.FirstOrDefault(row => row.IntervalId == selectedId);
        }
        if (BurnIntervalList.SelectedItem is null && dashboard.QuotaBurnIntervals.Count > 0)
        {
            BurnIntervalList.SelectedIndex = 0;
        }
        if (dashboard.QuotaBurnIntervals.Count == 0)
        {
            Interlocked.Increment(ref _selectionGeneration);
            ContributorList.ItemsSource = null;
            SelectedIntervalTitle.Text = "Select a quota-burn interval";
            SelectedIntervalFacts.Text = "Observed provider facts and estimated local contributors will appear here.";
            IntervalEvidenceText.Text = string.Empty;
        }

        _resetRows = dashboard.ResetEvents.Select(reset => new ResetRow(
                $"Observed {reset.DetectedAtUtc.ToLocalTime():G} · {FormatKind(reset.Kind)} · {FormatClassification(reset.Classification)} · " +
                $"{FormatNullablePercent(reset.BeforeUsedPercent)} → {FormatNullablePercent(reset.AfterUsedPercent)} used",
                $"{FormatNullablePercent(reset.BeforeUsedPercent)} → {FormatNullablePercent(reset.AfterUsedPercent)} used · " +
                (reset.Classification == QuotaResetClassification.ReanchoredWindow
                    ? "The reported reset deadline moved; this is not evidence of quota replenishment."
                    : "A drop was observed between readings; the exact reset time and cause are not established.") +
                (DisplayAuthority(reset.Source) == QuotaObservationAuthority.EmbeddedObservation
                    ? "\nThis session may have observed an earlier account change later. Separate rollout observations are not additional confirmed resets; unknown account ownership is not inferred."
                    : "") +
                $"\nPrevious reset deadline: {FormatReset(reset.PreviousResetAtUtc)}\nNew reset deadline: {FormatReset(reset.CurrentResetAtUtc)}" +
                (reset.PreviousResetAtUtc is { } before && reset.CurrentResetAtUtc is { } after
                    ? $"\nDeadline shift: {(after - before).TotalSeconds:+0.###;-0.###;0} seconds" : "") +
                $"\nSource: {reset.Source}\nSignal ID: {reset.EventId}",
                Scope: $"{SourceLabel(reset.Source)} · {QuotaAccountScope.Describe(reset.AccountKey)}", Source: reset.Source,
                IsTimeShift: reset.Classification == QuotaResetClassification.ReanchoredWindow))
                .ToArray();
        RenderResets();

        _burnStatus =
            SourceFilter.SelectedIndex == 0
                ? $"{dashboard.QuotaBurnIntervals.Count:N0} account-meter transitions · latest 80 at most. Times bracket when a change was observed, not when every token was consumed."
                : $"Diagnostic view: {dashboard.QuotaBurnIntervals.Count:N0} observations, NOT unique quota changes. Several sessions may report the same change; do not add their deltas or token totals.";
        if (BurnTabs.SelectedIndex == 0) StatusText.Text = _burnStatus;
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BurnSummary is null || BurnTabs is null) return;
        BurnSummary.Visibility = BurnTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (BurnTabs.SelectedIndex == 0) StatusText.Text = _loadError ?? _burnStatus;
        else RenderResets();
    }

    private sealed record TimelineGroup(object Key, string Title, IReadOnlyList<QuotaBurnInterval> Rows);
    private void OnTimelineSeriesChanged(object sender, SelectionChangedEventArgs e) => RenderTimeline();
    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e) => RenderTimeline();
    private void OnActivityOverlayChanged(object sender, RoutedEventArgs e) => RenderTimeline();

    private void RenderTimeline()
    {
        if (QuotaTimeline is null) return;
        QuotaTimeline.Children.Clear();
        if (TimelineSeries.SelectedItem is not TimelineGroup series || series.Rows.Count == 0)
        {
            TimelineCaption.Text = "No quota increases in this view. Missing observations do not imply zero consumption.";
            return;
        }
        var width = QuotaTimeline.ActualWidth - 50;
        if (width <= 0) return;
        var from = series.Rows.Min(x => x.StartUtc);
        var to = series.Rows.Max(x => x.EndUtc);
        var seconds = Math.Max(1, (to - from).TotalSeconds);
        double X(DateTimeOffset t) => 40 + Math.Clamp((t - from).TotalSeconds / seconds, 0, 1) * (width - 12);
        static double Y(double percent) => 115 - Math.Clamp(percent, 0, 100);
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        foreach (var percent in new[] { 0, 50, 100 })
        {
            var label = new TextBlock { Text = percent + "%", FontSize = 12, Foreground = secondary };
            Canvas.SetTop(label, Y(percent) - 8); QuotaTimeline.Children.Add(label);
        }
        var maxTokens = Math.Max(1, series.Rows.Max(x => x.NativeTokens));
        foreach (var row in series.Rows)
        {
            // Do not join separate observations, reset generations or source/account cohorts.
            var line = new Line { X1 = X(row.StartUtc), X2 = X(row.EndUtc), Y1 = Y(row.BeforeUsedPercent),
                Y2 = Y(row.AfterUsedPercent), Stroke = accent, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 } };
            QuotaTimeline.Children.Add(line);
            var description = $"{row.StartUtc.ToLocalTime():g} → {row.EndUtc.ToLocalTime():g}: {row.BeforeUsedPercent:0.#}% → {row.AfterUsedPercent:0.#}% used; {row.NativeTokens:N0} local tokens (not attribution)";
            var point = new Button { Content = "", Width = 14, Height = 14, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(0), CornerRadius = new CornerRadius(7), Background = accent };
            ToolTipService.SetToolTip(point, description); AutomationProperties.SetName(point, description);
            point.Click += (_, _) =>
            {
                BurnTabs.SelectedIndex = 0;
                BurnIntervalList.SelectedItem = BurnIntervalList.Items.OfType<BurnIntervalRow>().FirstOrDefault(x => x.IntervalId == row.IntervalId);
                if (BurnIntervalList.SelectedItem is { } selected) BurnIntervalList.ScrollIntoView(selected);
            };
            Canvas.SetLeft(point, X(row.EndUtc) - 7); Canvas.SetTop(point, Y(row.AfterUsedPercent) - 7);
            QuotaTimeline.Children.Add(point);
            if (ShowActivity.IsChecked == true)
            {
                var bar = new Border { Width = Math.Max(2, X(row.EndUtc) - X(row.StartUtc)), Height = 28d * row.NativeTokens / maxTokens,
                    Background = secondary, Opacity = 0.6 };
                Canvas.SetLeft(bar, X(row.StartUtc)); Canvas.SetTop(bar, 155 - bar.Height);
                ToolTipService.SetToolTip(bar, description); QuotaTimeline.Children.Add(bar);
            }
        }
        TimelineCaption.Text = $"{from.ToLocalTime():d MMM HH:mm} → {to.ToLocalTime():d MMM HH:mm} · local time. Select a point for details. " +
            "Dashed segments link observed endpoints, not exact change times; gaps are not interpolated." +
            (ShowActivity.IsChecked == true ? " Bars show local tokens relative to this series, not quota shares." : "");
    }

    private void OnResetFilterChanged(object sender, SelectionChangedEventArgs e) => RenderResets();

    private void RenderResets()
    {
        if (ResetList is null || ResetSourceFilter is null || ResetKindFilter is null) return;
        var rows = _resetRows.Where(row => ResetSourceFilter.SelectedIndex == 0 ||
            ResetSourceFilter.SelectedIndex == (DisplayAuthority(row.Source) switch
            {
                QuotaObservationAuthority.ProviderAuthoritative => 1,
                QuotaObservationAuthority.EmbeddedObservation => 2,
                _ => 3
            })).Where(row => ResetKindFilter.SelectedIndex == 2 ||
                row.IsTimeShift == (ResetKindFilter.SelectedIndex == 1)).ToArray();
        ResetList.ItemsSource = rows.Length > 0 ? rows :
            new[] { new ResetRow("No matching observations in the loaded history", "Try another change type, source or a longer range. Missing evidence is not proof that no reset occurred.") };
        if (BurnTabs?.SelectedIndex == 1)
            StatusText.Text = _loadError ?? $"{rows.Length:N0} observations in this view · up to 200 loaded across sources. Observation times are not confirmed reset times; rollout sessions can repeat the same change.";
    }

    private static QuotaObservationAuthority DisplayAuthority(string source) =>
        source.Contains('→') ? QuotaObservationAuthority.Unknown : QuotaSnapshot.ClassifyAuthority(source);

    private static string SourceLabel(string source) => DisplayAuthority(source) switch
    {
        QuotaObservationAuthority.ProviderAuthoritative => "Account meter",
        QuotaObservationAuthority.EmbeddedObservation => "Rollout reading",
        _ => "Unknown / mixed source"
    };

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) =>
        OnRefreshClicked(sender, new RoutedEventArgs());

    private async void OnBurnIntervalSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SourceDetails.IsExpanded = ActivityDetails.IsExpanded = false;
        var generation = Interlocked.Increment(ref _selectionGeneration);
        var cancellation = _pageCancellation;
        if (!_isLoaded || cancellation is null || cancellation.IsCancellationRequested ||
            BurnIntervalList.SelectedItem is not BurnIntervalRow row || string.IsNullOrEmpty(row.IntervalId) ||
            !_intervalsById.TryGetValue(row.IntervalId, out var interval))
        {
            ContributorList.ItemsSource = null;
            SelectedIntervalTitle.Text = "Select a quota-burn interval";
            SelectedIntervalFacts.Text = "Observed provider facts and estimated local contributors will appear here.";
            IntervalEvidenceText.Text = string.Empty;
            return;
        }

        SelectedIntervalTitle.Text =
            $"{FormatKind(interval.Kind)} · {interval.BeforeUsedPercent:0.#}% → {interval.AfterUsedPercent:0.#}% used";
        SelectedIntervalFacts.Text =
            $"{interval.StartUtc.ToLocalTime():dd MMM yyyy HH:mm:ss zzz} → {interval.EndUtc.ToLocalTime():dd MMM yyyy HH:mm:ss zzz}\n" +
            $"+{interval.DeltaUsedPercent:0.#} percentage points observed over {FormatDuration(interval.EndUtc - interval.StartUtc)}";
        IntervalEvidenceText.Text =
            $"{interval.StartUtc.ToLocalTime():G} → {interval.EndUtc.ToLocalTime():G}\n" +
            $"Source: {interval.AfterSource}\nProvider/profile label: {interval.Provider}/{interval.Profile}\n{QuotaAccountScope.Describe(interval.AccountKey)}\nReset: {FormatReset(interval.ResetsAtUtc)}\n\n" +
            "Local activity is not verified to belong to this backend account. The meter can be rounded or delayed. Session activity is correlated with the interval; it does not establish per-session quota costs. Activity bars show local token shares, not quota shares.";
        ContributorList.ItemsSource = new[] { new ContributorRow("Loading estimated contributors…", "Querying only the selected interval.") };

        try
        {
            var detail = await Task.Run(
                () => App.Services.Intelligence.GetQuotaBurnDetailAsync(interval, 30, cancellation.Token),
                cancellation.Token);
            if (!_isLoaded || cancellation.IsCancellationRequested || generation != Volatile.Read(ref _selectionGeneration))
            {
                return;
            }

            ContributorList.ItemsSource = detail.Contributors.Count == 0
                ? new[] { new ContributorRow("No local activity found", "The quota change is observed, but this installation has no token activity recorded in that interval.") }
                : detail.Contributors.OrderByDescending(contributor => contributor.NativeTokens).Select(contributor => new ContributorRow(
                    $"{contributor.DisplayName} · {contributor.TokenShare:P0} of local tokens",
                    $"{(contributor.IsSubagent ? "Subagent" : "Root")} · {contributor.Repository}\n" +
                    $"{FormatCount(contributor.NativeTokens)} tokens · {FormatCount(contributor.UncachedInputTokens)} uncached · {FormatCount(contributor.CacheReadTokens)} cached" +
                    (string.IsNullOrWhiteSpace(contributor.Model) ? string.Empty : $" · {contributor.Model}") +
                    (string.IsNullOrWhiteSpace(contributor.ReasoningEffort) ? string.Empty : $" · {contributor.ReasoningEffort}"),
                    contributor.TokenShare * 100))
                    .ToArray();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_isLoaded && generation == Volatile.Read(ref _selectionGeneration))
            {
                ContributorList.ItemsSource = new[] { new ContributorRow("Contributor query failed", Summarize(exception.Message)) };
            }
        }
    }

    private int ParseDays()
    {
        if (RangeCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var days))
        {
            return Math.Clamp(days, 1, 3650);
        }
        return 7;
    }

    private static string FormatKind(QuotaWindowKind kind) =>
        kind == QuotaWindowKind.FiveHour ? "5h" : kind == QuotaWindowKind.Weekly ? "weekly" : kind.ToString();

    private static string FormatClassification(QuotaResetClassification classification) => classification switch
    {
        QuotaResetClassification.ExpectedReset => "quota drop near reset deadline",
        QuotaResetClassification.ReanchoredWindow => "reset-time shift · no replenishment established",
        QuotaResetClassification.UnusualReset => "quota drop",
        QuotaResetClassification.FullReset => "large quota drop",
        _ => classification.ToString()
    };

    private static string FormatNullablePercent(double? value) => value is double percent ? $"{percent:0.#}%" : "?";

    private static string FormatReset(DateTimeOffset? reset) =>
        reset is DateTimeOffset value ? value.ToLocalTime().ToString("G") : "unknown";

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? $"{duration.TotalHours:0.#}h" : duration.TotalMinutes >= 1
            ? $"{duration.TotalMinutes:0.#}m" : duration.TotalSeconds >= 1 ? $"{duration.TotalSeconds:0.#}s" : "<1s";

    private static string FormatCount(long value)
    {
        var absolute = Math.Abs((double)value);
        return absolute switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000d:0.00}B",
            >= 1_000_000 => $"{value / 1_000_000d:0.0}M",
            >= 1_000 => $"{value / 1_000d:0.0}K",
            _ => value.ToString("N0")
        };
    }

    private static string Summarize(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 320 ? value : value[..320] + "…";
    }

    private sealed record BurnIntervalRow(string IntervalId, string Header, string Detail, string? Scope = null);
    private sealed record ContributorRow(string Header, string Detail, double Share = 0);
    private sealed record ResetRow(string Header, string Detail, string? Scope = null, string Source = "", bool IsTimeShift = false);
}
