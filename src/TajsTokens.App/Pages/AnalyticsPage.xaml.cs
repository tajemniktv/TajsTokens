using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    public AnalyticsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
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
                StatusText.Text = $"Quota intelligence unavailable: {Summarize(exception.Message)}";
            }
        }
    }

    private void Render(IntelligenceDashboard dashboard)
    {
        var selectedId = (BurnIntervalList.SelectedItem as BurnIntervalRow)?.IntervalId;
        _intervalsById.Clear();
        foreach (var interval in dashboard.QuotaBurnIntervals)
        {
            _intervalsById[interval.IntervalId] = interval;
        }

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
            $"+{interval.DeltaUsedPercent:0.#} pp · {FormatKind(interval.Kind)} · {interval.EndUtc.ToLocalTime():dd MMM HH:mm:ss}",
            $"Over {FormatDuration(interval.EndUtc - interval.StartUtc)} · {FormatCount(interval.NativeTokens)} local tokens · " +
            (QuotaSnapshot.ClassifyAuthority(interval.AfterSource) == QuotaObservationAuthority.ProviderAuthoritative ? "account meter" : "rollout reading")))
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
        }

        ResetList.ItemsSource = dashboard.ResetEvents.Count == 0
            ? new[] { new ResetRow("No reset/re-anchor events", "No provider history in this range met the reset detector's evidence thresholds.") }
            : dashboard.ResetEvents.Select(reset => new ResetRow(
                $"{reset.EffectiveAtUtc.ToLocalTime():g} · {FormatKind(reset.Kind)} · {FormatClassification(reset.Classification)}",
                $"{FormatNullablePercent(reset.BeforeUsedPercent)} → {FormatNullablePercent(reset.AfterUsedPercent)} used · " +
                $"{reset.Source} · {reset.Explanation}"))
                .ToArray();

        StatusText.Text =
            $"Showing {dashboard.QuotaBurnIntervals.Count:N0} recent changes · up to 80 per view. Sources are never joined to calculate a change.";
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) =>
        OnRefreshClicked(sender, new RoutedEventArgs());

    private async void OnBurnIntervalSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var generation = Interlocked.Increment(ref _selectionGeneration);
        var cancellation = _pageCancellation;
        if (!_isLoaded || cancellation is null || cancellation.IsCancellationRequested ||
            BurnIntervalList.SelectedItem is not BurnIntervalRow row || string.IsNullOrEmpty(row.IntervalId) ||
            !_intervalsById.TryGetValue(row.IntervalId, out var interval))
        {
            ContributorList.ItemsSource = null;
            SelectedIntervalTitle.Text = "Select a quota-burn interval";
            SelectedIntervalFacts.Text = "Observed provider facts and estimated local contributors will appear here.";
            return;
        }

        SelectedIntervalTitle.Text =
            $"{FormatKind(interval.Kind)} · {interval.BeforeUsedPercent:0.#}% → {interval.AfterUsedPercent:0.#}% used";
        SelectedIntervalFacts.Text =
            $"+{interval.DeltaUsedPercent:0.#} quota points over {FormatDuration(interval.EndUtc - interval.StartUtc)}\n" +
            $"{FormatCount(interval.NativeTokens)} local tokens · {interval.RootSessions} root / {interval.SubagentSessions} subagent sessions";
        IntervalEvidenceText.Text =
            $"{interval.StartUtc.ToLocalTime():G} → {interval.EndUtc.ToLocalTime():G}\n" +
            $"Source: {interval.AfterSource}\nAccount profile: {interval.Provider}/{interval.Profile}\nReset: {FormatReset(interval.ResetsAtUtc)}\n\n" +
            "The meter can be rounded or delayed. Session activity is correlated with the interval; it does not establish per-session quota costs. Activity bars show shares of locally recorded tokens, not shares of quota.";
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
        QuotaResetClassification.ExpectedReset => "expected reset",
        QuotaResetClassification.ReanchoredWindow => "re-anchored window",
        QuotaResetClassification.UnusualReset => "unusual reset evidence",
        QuotaResetClassification.FullReset => "full-reset evidence",
        _ => classification.ToString()
    };

    private static string FormatNullablePercent(double? value) => value is double percent ? $"{percent:0.#}%" : "?";

    private static string FormatReset(DateTimeOffset? reset) =>
        reset is DateTimeOffset value ? value.ToLocalTime().ToString("g") : "unknown";

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

    private sealed record BurnIntervalRow(string IntervalId, string Header, string Detail);
    private sealed record ContributorRow(string Header, string Detail, double Share = 0);
    private sealed record ResetRow(string Header, string Detail);
}
