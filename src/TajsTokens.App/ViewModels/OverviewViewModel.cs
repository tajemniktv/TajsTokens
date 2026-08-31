using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.App.Models;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.App.ViewModels;

public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly ITokscaleProvider _tokscaleProvider;
    private readonly ICodexQuotaProvider _quotaProvider;
    private readonly SqliteTelemetryRepository _repository;
    private readonly ForecastingService _forecastingService = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    [ObservableProperty]
    public partial QuotaCardViewModel FiveHourQuota { get; set; } = UnavailableQuota("5-hour quota", "Waiting for first refresh.");

    [ObservableProperty]
    public partial QuotaCardViewModel WeeklyQuota { get; set; } = UnavailableQuota("Weekly quota", "Waiting for first refresh.");

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Waiting for local telemetry providers.";

    [ObservableProperty]
    public partial string LastUpdatedText { get; set; } = "Not refreshed yet";

    [ObservableProperty]
    public partial string HistoryCaption { get; set; } = "Tokscale hourly history will appear after the first successful refresh.";

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    public ObservableCollection<TokenSummaryCard> TokenSummaryCards { get; } = [];
    public ObservableCollection<ForecastPoint> HistoryPoints { get; } = [];
    public ObservableCollection<DataSourceStatusCard> DataSources { get; } = [];
    public ObservableCollection<EventItem> RecentEvents { get; } = [];

    public OverviewViewModel(
        ITokscaleProvider tokscaleProvider,
        ICodexQuotaProvider quotaProvider,
        SqliteTelemetryRepository repository)
    {
        _tokscaleProvider = tokscaleProvider;
        _quotaProvider = quotaProvider;
        _repository = repository;
    }

    [RelayCommand]
    private Task RefreshCommandAsync() => RefreshAsync(CancellationToken.None);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        IsRefreshing = true;
        StatusText = "Refreshing local telemetry…";
        DataSources.Clear();
        RecentEvents.Clear();

        var quotaSucceeded = false;
        var tokscaleSucceeded = false;

        try
        {
            await _repository.InitializeAsync(cancellationToken);

            // Quota collection starts immediately and runs while Tokscale scans local session files.
            var quotaTask = _quotaProvider.GetQuotaSnapshotsAsync(cancellationToken);

            IReadOnlyList<TokenUsage> usages = [];
            try
            {
                usages = await _tokscaleProvider.GetUsageObservationsAsync(cancellationToken);
                RenderTokenSummary(usages);

                var hourly = await _tokscaleProvider.GetHourlyUsageAsync(cancellationToken);
                RenderHourlyHistory(hourly);
                tokscaleSucceeded = true;
                DataSources.Add(new DataSourceStatusCard(
                    "Tokscale",
                    "Live",
                    $"{usages.Count} model row(s), {hourly.Count} hourly bucket(s) from local Codex sessions."));
                AddEvent("Token refresh", $"Loaded {FormatTokenCount(usages.Sum(item => item.Breakdown.Total))} tokens from Tokscale.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var tokscaleError = SummarizeError(exception);
                RenderTokenUnavailable();
                DataSources.Add(new DataSourceStatusCard("Tokscale", "Unavailable", tokscaleError));
                AddEvent("Tokscale unavailable", tokscaleError);
            }

            try
            {
                var snapshots = await quotaTask;
                foreach (var snapshot in snapshots)
                {
                    await _repository.UpsertQuotaSnapshotAsync(snapshot, cancellationToken);
                }

                await RenderQuotaAsync(QuotaWindowKind.FiveHour, snapshots, cancellationToken);
                await RenderQuotaAsync(QuotaWindowKind.Weekly, snapshots, cancellationToken);
                quotaSucceeded = snapshots.Count > 0;

                DataSources.Add(new DataSourceStatusCard(
                    "Codex app-server",
                    quotaSucceeded ? "Live" : "No windows",
                    quotaSucceeded
                        ? $"{snapshots.Count} provider-authoritative quota window(s). No model turn was created."
                        : "The app-server responded but did not expose a supported quota window."));
                AddEvent("Quota refresh", quotaSucceeded
                    ? "Captured provider-authoritative Codex quota and reset timestamps."
                    : "Codex app-server returned no supported quota windows.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var quotaError = SummarizeError(exception);
                FiveHourQuota = UnavailableQuota("5-hour quota", quotaError);
                WeeklyQuota = UnavailableQuota("Weekly quota", quotaError);
                DataSources.Add(new DataSourceStatusCard("Codex app-server", "Unavailable", quotaError));
                AddEvent("Quota unavailable", quotaError);
            }

            var localNow = DateTimeOffset.Now;
            LastUpdatedText = $"Updated {localNow:HH:mm:ss}";
            StatusText = (tokscaleSucceeded, quotaSucceeded) switch
            {
                (true, true) => "Live local telemetry",
                (true, false) or (false, true) => "Partial telemetry",
                _ => "Telemetry unavailable"
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var error = SummarizeError(exception);
            StatusText = "Local telemetry database unavailable";
            LastUpdatedText = "Refresh failed";
            DataSources.Add(new DataSourceStatusCard("SQLite", "Error", error));
            AddEvent("Persistence error", error);
        }
        finally
        {
            IsRefreshing = false;
            _refreshGate.Release();
        }
    }

    private async Task RenderQuotaAsync(
        QuotaWindowKind kind,
        IReadOnlyList<QuotaSnapshot> currentSnapshots,
        CancellationToken cancellationToken)
    {
        var current = currentSnapshots
            .Where(snapshot => snapshot.Kind == kind)
            .OrderByDescending(snapshot => snapshot.CapturedAtUtc)
            .FirstOrDefault();

        var title = kind == QuotaWindowKind.FiveHour ? "5-hour quota" : "Weekly quota";
        if (current is null)
        {
            if (kind == QuotaWindowKind.FiveHour)
            {
                FiveHourQuota = UnavailableQuota(title, "Codex did not expose this window on the latest refresh.");
            }
            else
            {
                WeeklyQuota = UnavailableQuota(title, "Codex did not expose this window on the latest refresh.");
            }
            return;
        }

        var history = await _repository.GetRecentQuotaSnapshotsAsync(
            kind,
            current.Provider,
            current.Profile,
            96,
            cancellationToken);

        Forecast? forecast = null;
        try
        {
            forecast = _forecastingService.BuildForecast(history, DateTimeOffset.UtcNow);
        }
        catch (ArgumentException)
        {
            // One valid current snapshot is still useful even if historical data is not forecastable.
        }

        var card = BuildQuotaCard(title, current, forecast);
        if (kind == QuotaWindowKind.FiveHour)
        {
            FiveHourQuota = card;
        }
        else
        {
            WeeklyQuota = card;
        }
    }

    private void RenderTokenSummary(IReadOnlyList<TokenUsage> usages)
    {
        TokenSummaryCards.Clear();
        var uncached = usages.Sum(item => item.Breakdown.UncachedInput);
        var cacheRead = usages.Sum(item => item.Breakdown.CacheRead);
        var cacheWrite = usages.Sum(item => item.Breakdown.CacheWrite);
        var output = usages.Sum(item => item.Breakdown.NonReasoningOutput);
        var reasoning = usages.Sum(item => item.Breakdown.ReasoningOutput);
        var total = usages.Sum(item => item.Breakdown.Total);

        TokenSummaryCards.Add(new TokenSummaryCard("Uncached input", FormatTokenCount(uncached), "Tokscale · disjoint input"));
        TokenSummaryCards.Add(new TokenSummaryCard("Cache read", FormatTokenCount(cacheRead), "Tokscale · cached input"));
        if (cacheWrite > 0)
        {
            TokenSummaryCards.Add(new TokenSummaryCard("Cache write", FormatTokenCount(cacheWrite), "Tokscale · cache writes"));
        }
        TokenSummaryCards.Add(new TokenSummaryCard("Output", FormatTokenCount(output), "Excludes reasoning"));
        TokenSummaryCards.Add(new TokenSummaryCard("Reasoning", FormatTokenCount(reasoning), "Separate reasoning output"));
        TokenSummaryCards.Add(new TokenSummaryCard("Total", FormatTokenCount(total), $"{usages.Count} Codex model row(s)"));
    }

    private void RenderTokenUnavailable()
    {
        TokenSummaryCards.Clear();
        TokenSummaryCards.Add(new TokenSummaryCard("Token accounting", "Unavailable", "Install/update Tokscale or inspect provider status below."));
        HistoryPoints.Clear();
        HistoryCaption = "Hourly history unavailable because Tokscale could not be read.";
    }

    private void RenderHourlyHistory(IReadOnlyList<TokenTimeBucket> buckets)
    {
        HistoryPoints.Clear();
        var visible = buckets.TakeLast(12).ToArray();
        if (visible.Length == 0)
        {
            HistoryCaption = "Tokscale returned no hourly Codex buckets for the current report range.";
            return;
        }

        var max = visible.Max(bucket => bucket.Breakdown.Total);
        foreach (var bucket in visible)
        {
            var height = max <= 0 ? 10d : 18d + (92d * bucket.Breakdown.Total / max);
            HistoryPoints.Add(new ForecastPoint(
                CompactBucketLabel(bucket.Label),
                height,
                $"{bucket.Label} · {FormatTokenCount(bucket.Breakdown.Total)}"));
        }

        HistoryCaption = $"Real Tokscale hourly usage · last {visible.Length} bucket(s) · bars normalized to the busiest visible hour.";
    }

    private static QuotaCardViewModel BuildQuotaCard(string title, QuotaSnapshot snapshot, Forecast? forecast)
    {
        var remaining = snapshot.RemainingPercent;
        var burn = forecast?.BurnRatePercentPerHour;
        var resetCountdown = snapshot.ResetsAtUtc is DateTimeOffset reset
            ? FormatTimeSpan(reset - DateTimeOffset.UtcNow)
            : "Unknown";
        var exhaustion = forecast?.EstimatedExhaustionAtUtc is DateTimeOffset exhaustionAt
            ? exhaustionAt.ToLocalTime().ToString("ddd HH:mm")
            : "Learning from history";

        var survivalMessage = forecast?.SurvivesUntilReset switch
        {
            true => "Current burn is projected to survive until reset.",
            false => "Current burn is projected to exhaust before reset.",
            _ => "Quota is live; more history is needed for a burn forecast."
        };
        var severity = forecast?.SurvivesUntilReset switch
        {
            false => InfoBarSeverity.Warning,
            true => InfoBarSeverity.Success,
            _ => InfoBarSeverity.Informational
        };

        return new QuotaCardViewModel(
            title,
            remaining is double value ? $"{value:0.#}%" : "Unknown",
            resetCountdown,
            burn is double rate ? $"{rate:0.0} pp/h" : "Learning",
            exhaustion,
            remaining is double gauge ? $"{gauge:0.#}% remaining · {snapshot.Source}" : snapshot.Source,
            survivalMessage,
            severity);
    }

    private static QuotaCardViewModel UnavailableQuota(string title, string detail) =>
        new(title, "Unavailable", "Unknown", "Unavailable", "Unavailable", "No live quota data", detail, InfoBarSeverity.Warning);

    private void AddEvent(string type, string description) =>
        RecentEvents.Insert(0, new EventItem("Now", type, description));

    private static string CompactBucketLabel(string label)
    {
        if (label.Length <= 8)
        {
            return label;
        }

        var separator = label.LastIndexOf(' ');
        return separator >= 0 && separator < label.Length - 1 ? label[(separator + 1)..] : label[^8..];
    }

    private static string FormatTokenCount(long value)
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

    private static string FormatTimeSpan(TimeSpan timeSpan)
    {
        if (timeSpan <= TimeSpan.Zero)
        {
            return "due now";
        }

        if (timeSpan.TotalDays >= 1)
        {
            return $"{(int)timeSpan.TotalDays}d {timeSpan.Hours}h";
        }

        return $"{(int)timeSpan.TotalHours}h {timeSpan.Minutes}m";
    }

    private static string SummarizeError(Exception exception)
    {
        var message = exception.Message.ReplaceLineEndings(" ").Trim();
        return message.Length <= 240 ? message : message[..240] + "…";
    }
}
