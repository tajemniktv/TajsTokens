using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.App.Pages;

public sealed partial class UsagePage : Page
{
    private CancellationTokenSource? _pageCancellation;
    private bool _isLoaded;
    private long _loadGeneration;

    public UsagePage()
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
            StatusText.Text = "Aggregating local telemetry…";
            var now = DateTimeOffset.UtcNow;
            var days = ParseDays();
            var requestedBucket = ParseBucket();
            var query = new IntelligenceQuery(now.AddDays(-days), now, requestedBucket, 720);
            var dashboard = await Task.Run(
                () => App.Services.Intelligence.QueryAsync(query, cancellationToken),
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
                StatusText.Text = $"Usage analytics unavailable: {Summarize(exception.Message)}";
            }
        }
    }

    private void Render(IntelligenceDashboard dashboard)
    {
        var totalTokens = dashboard.UsageHistory.Sum(bucket => bucket.NativeTokens);
        var rootTokens = dashboard.UsageHistory.Sum(bucket => bucket.RootTokens);
        var subagentTokens = dashboard.UsageHistory.Sum(bucket => bucket.SubagentTokens);
        var fiveHourDelta = dashboard.UsageHistory.Sum(bucket => bucket.FiveHourQuotaDelta ?? 0);
        var weeklyDelta = dashboard.UsageHistory.Sum(bucket => bucket.WeeklyQuotaDelta ?? 0);

        NativeTokensText.Text = FormatCount(totalTokens);
        RoleTokensText.Text = $"{FormatCount(rootTokens)} / {FormatCount(subagentTokens)}";
        FiveHourDeltaText.Text = fiveHourDelta > 0 ? $"+{fiveHourDelta:0.#} pp" : "No observed rise";
        WeeklyDeltaText.Text = weeklyDelta > 0 ? $"+{weeklyDelta:0.#} pp" : "No observed rise";

        HistoryList.ItemsSource = dashboard.UsageHistory.Count == 0
            ? new[] { new HistoryRow("No history", "—", "No native Codex token events are available in this range yet.") }
            : dashboard.UsageHistory.Select(bucket => new HistoryRow(
                FormatBucket(bucket.StartUtc, dashboard.Query.BucketSize),
                FormatCount(bucket.NativeTokens),
                $"root {FormatCount(bucket.RootTokens)} · subagents {FormatCount(bucket.SubagentTokens)} · " +
                $"sessions {bucket.ActiveSessions:N0} · compactions {bucket.Compactions:N0}" +
                (bucket.FiveHourQuotaDelta is double five ? $" · 5h +{five:0.#}pp" : string.Empty) +
                (bucket.WeeklyQuotaDelta is double week ? $" · weekly +{week:0.#}pp" : string.Empty))).ToArray();

        DimensionList.ItemsSource = dashboard.Dimensions.Count == 0
            ? new[] { new DimensionRow("History", "No breakdowns", "—") }
            : dashboard.Dimensions.Select(item => new DimensionRow(
                item.Dimension,
                item.Value,
                $"{FormatCount(item.NativeTokens)} · {item.Sessions:N0} session(s) · cache {FormatPercent(item.CacheReadTokens, item.UncachedInputTokens + item.CacheReadTokens)}")).ToArray();

        var maxHeat = dashboard.Heatmap.Count == 0 ? 0L : dashboard.Heatmap.Max(cell => cell.NativeTokens);
        HeatmapList.ItemsSource = dashboard.Heatmap.Count == 0
            ? new[] { new HeatmapRow("No heatmap", 0, "—") }
            : dashboard.Heatmap
                .OrderBy(cell => ((int)cell.Day + 6) % 7) // Monday first without changing UTC semantics.
                .ThenBy(cell => cell.Hour)
                .Select(cell => new HeatmapRow(
                    $"{cell.Day} {cell.Hour:00}:00 UTC",
                    maxHeat <= 0 ? 0 : 100d * cell.NativeTokens / maxHeat,
                    $"{FormatCount(cell.NativeTokens)} · {cell.ActiveBuckets}h"))
                .ToArray();

        StatusText.Text =
            $"{dashboard.Query.FromUtc.ToLocalTime():g} → {dashboard.Query.ToUtc.ToLocalTime():g} · " +
            $"{dashboard.Query.BucketSize.ToString().ToLowerInvariant()} buckets · {dashboard.UsageHistory.Count:N0} populated bucket(s). " +
            "Native accounting remains shadow data until Phase 6 parity/cutover.";
    }

    private int ParseDays()
    {
        if (RangeCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var days))
        {
            return Math.Clamp(days, 1, 3650);
        }
        return 7;
    }

    private AnalyticsBucketSize ParseBucket()
    {
        if (BucketCombo.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<AnalyticsBucketSize>(item.Tag?.ToString(), out var bucket))
        {
            return bucket;
        }
        return AnalyticsBucketSize.Hour;
    }

    private static string FormatBucket(DateTimeOffset value, AnalyticsBucketSize size) => size switch
    {
        AnalyticsBucketSize.Minute => value.ToLocalTime().ToString("ddd HH:mm"),
        AnalyticsBucketSize.Hour => value.ToLocalTime().ToString("ddd HH:00"),
        _ => value.ToLocalTime().ToString("yyyy-MM-dd")
    };

    private static string FormatPercent(long numerator, long denominator) =>
        denominator <= 0 ? "n/a" : $"{100d * numerator / denominator:0.#}%";

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
        return value.Length <= 260 ? value : value[..260] + "…";
    }

    private sealed record HistoryRow(string Label, string Tokens, string Detail);
    private sealed record DimensionRow(string Dimension, string Value, string Detail);
    private sealed record HeatmapRow(string Label, double Intensity, string Detail);
}
