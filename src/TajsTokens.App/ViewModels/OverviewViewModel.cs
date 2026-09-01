using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.App.Models;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.App.ViewModels;

public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly TelemetryCoordinator _telemetry;
    private readonly SqliteTelemetryRepository _repository;
    private readonly ForecastingService _forecastingService = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private long _newestRequestedSnapshotTicks = DateTimeOffset.MinValue.UtcDateTime.Ticks;

    [ObservableProperty]
    private QuotaCardViewModel fiveHourQuota = UnavailableQuota("5-hour quota", "Waiting for first background refresh.");

    [ObservableProperty]
    private QuotaCardViewModel weeklyQuota = UnavailableQuota("Weekly quota", "Waiting for first background refresh.");

    [ObservableProperty]
    private string statusText = "Waiting for background telemetry.";

    [ObservableProperty]
    private string lastUpdatedText = "Not refreshed yet";

    [ObservableProperty]
    private string historyCaption = "Tokscale hourly history will appear after the first successful refresh.";

    [ObservableProperty]
    private bool isRefreshing;

    public ObservableCollection<TokenSummaryCard> TokenSummaryCards { get; } = [];
    public ObservableCollection<ForecastPoint> HistoryPoints { get; } = [];
    public ObservableCollection<DataSourceStatusCard> DataSources { get; } = [];
    public ObservableCollection<EventItem> RecentEvents { get; } = [];
    public IAsyncRelayCommand RefreshCommand { get; }

    public OverviewViewModel(TelemetryCoordinator telemetry, SqliteTelemetryRepository repository)
    {
        _telemetry = telemetry;
        _repository = repository;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsRefreshing = true;
        StatusText = "Refreshing shared telemetry…";
        try
        {
            var snapshot = await _telemetry.RefreshAsync(RefreshTrigger.Manual, cancellationToken);
            await ApplySnapshotAsync(snapshot, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            StatusText = "Refresh failed";
            LastUpdatedText = "Refresh failed";
            AddEvent("Unexpected refresh error", SummarizeError(exception));
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public async Task ApplySnapshotAsync(TelemetrySnapshot snapshot, CancellationToken cancellationToken)
    {
        var snapshotTicks = snapshot.CapturedAtUtc.UtcDateTime.Ticks;
        RegisterNewestRequestedSnapshot(snapshotTicks);

        await _applyGate.WaitAsync(cancellationToken);
        try
        {
            // Event dispatch, initial page load and manual refresh can all request renders. Reject an
            // older snapshot if a newer coordinator snapshot was already requested before this one
            // acquired the render gate, so an awaited SQLite history read cannot restore stale UI.
            if (IsSuperseded(snapshotTicks))
            {
                return;
            }

            DataSources.Clear();
            foreach (var source in snapshot.Sources)
            {
                DataSources.Add(new DataSourceStatusCard(
                    source.Provider,
                    FormatHealth(source.State),
                    source.Detail));
            }

            RecentEvents.Clear();
            foreach (var telemetryEvent in snapshot.Events.OrderByDescending(item => item.TimestampUtc))
            {
                RecentEvents.Add(new EventItem(
                    telemetryEvent.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
                    telemetryEvent.Type,
                    telemetryEvent.Description));
            }

            if (snapshot.TokenDataFresh || snapshot.TokenUsages.Count > 0 || snapshot.HourlyBuckets.Count > 0)
            {
                RenderTokenSummary(snapshot.TokenUsages, snapshot.TokenDataFresh);
                RenderHourlyHistory(snapshot.HourlyBuckets, snapshot.TokenDataFresh);
            }
            else
            {
                RenderTokenUnavailable();
            }

            await RenderQuotaAsync(QuotaWindowKind.FiveHour, snapshot, cancellationToken);
            if (IsSuperseded(snapshotTicks))
            {
                return;
            }

            await RenderQuotaAsync(QuotaWindowKind.Weekly, snapshot, cancellationToken);
            if (IsSuperseded(snapshotTicks))
            {
                return;
            }

            LastUpdatedText = snapshot.CapturedAtUtc == DateTimeOffset.MinValue
                ? "Not refreshed yet"
                : $"Checked {snapshot.CapturedAtUtc.ToLocalTime():HH:mm:ss}";

            StatusText = (snapshot.TokenDataFresh, snapshot.QuotaDataFresh, snapshot.PersistenceAvailable, snapshot.HasAnyData) switch
            {
                (true, true, true, _) => "Live background telemetry",
                (true, true, false, _) => "Live telemetry · history unavailable",
                (true, false, _, _) or (false, true, _, _) => "Partial telemetry",
                (false, false, _, true) => "Stale telemetry · using last known good data",
                _ => "Telemetry unavailable"
            };
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private void RegisterNewestRequestedSnapshot(long snapshotTicks)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _newestRequestedSnapshotTicks);
            if (snapshotTicks <= observed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _newestRequestedSnapshotTicks, snapshotTicks, observed) == observed)
            {
                return;
            }
        }
    }

    private bool IsSuperseded(long snapshotTicks) =>
        snapshotTicks < Volatile.Read(ref _newestRequestedSnapshotTicks);

    private async Task RenderQuotaAsync(
        QuotaWindowKind kind,
        TelemetrySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var current = snapshot.QuotaSnapshots
            .Where(item => item.Kind == kind)
            .OrderByDescending(item => item.CapturedAtUtc)
            .FirstOrDefault();

        var title = kind == QuotaWindowKind.FiveHour ? "5-hour quota" : "Weekly quota";
        if (current is null)
        {
            SetQuotaCard(kind, UnavailableQuota(title, "No quota sample has been observed yet."));
            return;
        }

        Forecast? forecast = null;
        if (snapshot.PersistenceAvailable && snapshot.QuotaDataFresh)
        {
            try
            {
                var history = await _repository.GetRecentQuotaSnapshotsAsync(
                    kind,
                    current.Provider,
                    current.Profile,
                    96,
                    cancellationToken);

                try
                {
                    forecast = _forecastingService.BuildForecast(history, DateTimeOffset.UtcNow);
                }
                catch (ArgumentException)
                {
                    // Current quota remains useful while forecasting learns from more history.
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AddEvent("History unavailable", $"Live quota is still shown: {SummarizeError(exception)}");
            }
        }

        SetQuotaCard(kind, BuildQuotaCard(title, current, forecast, snapshot.QuotaDataFresh));
    }

    private void SetQuotaCard(QuotaWindowKind kind, QuotaCardViewModel card)
    {
        if (kind == QuotaWindowKind.FiveHour)
        {
            FiveHourQuota = card;
        }
        else if (kind == QuotaWindowKind.Weekly)
        {
            WeeklyQuota = card;
        }
    }

    private void RenderTokenSummary(IReadOnlyList<TokenUsage> usages, bool isFresh)
    {
        TokenSummaryCards.Clear();
        var uncached = usages.Sum(item => item.Breakdown.UncachedInput);
        var cacheRead = usages.Sum(item => item.Breakdown.CacheRead);
        var cacheWrite = usages.Sum(item => item.Breakdown.CacheWrite);
        var output = usages.Sum(item => item.Breakdown.NonReasoningOutput);
        var reasoning = usages.Sum(item => item.Breakdown.ReasoningOutput);
        var total = usages.Sum(item => item.Breakdown.Total);
        var provenance = isFresh ? "Tokscale · live" : "Tokscale · last known good";

        TokenSummaryCards.Add(new TokenSummaryCard("Uncached input", FormatTokenCount(uncached), $"{provenance} · disjoint input"));
        TokenSummaryCards.Add(new TokenSummaryCard("Cache read", FormatTokenCount(cacheRead), $"{provenance} · cached input"));
        if (cacheWrite > 0)
        {
            TokenSummaryCards.Add(new TokenSummaryCard("Cache write", FormatTokenCount(cacheWrite), $"{provenance} · cache writes"));
        }
        TokenSummaryCards.Add(new TokenSummaryCard("Output", FormatTokenCount(output), "Excludes reasoning"));
        TokenSummaryCards.Add(new TokenSummaryCard("Reasoning", FormatTokenCount(reasoning), "Separate reasoning output"));
        TokenSummaryCards.Add(new TokenSummaryCard("Total", FormatTokenCount(total), $"{usages.Count} Codex model row(s)"));
    }

    private void RenderTokenUnavailable()
    {
        TokenSummaryCards.Clear();
        TokenSummaryCards.Add(new TokenSummaryCard("Token accounting", "Unavailable", "Tokscale has not produced a successful snapshot yet."));
        HistoryPoints.Clear();
        HistoryCaption = "Hourly history is unavailable until Tokscale can be read.";
    }

    private void RenderHourlyHistory(IReadOnlyList<TokenTimeBucket> buckets, bool isFresh)
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

        var freshness = isFresh ? "live" : "stale";
        HistoryCaption = $"Tokscale hourly usage · {freshness} · last {visible.Length} bucket(s) · bars normalized to the busiest visible hour.";
    }

    private static QuotaCardViewModel BuildQuotaCard(
        string title,
        QuotaSnapshot snapshot,
        Forecast? forecast,
        bool isFresh)
    {
        var remaining = snapshot.RemainingPercent;
        var resetCountdown = snapshot.ResetsAtUtc is DateTimeOffset reset
            ? FormatTimeSpan(reset - DateTimeOffset.UtcNow)
            : "Unknown";

        if (!isFresh)
        {
            return new QuotaCardViewModel(
                title,
                remaining is double staleValue ? $"{staleValue:0.#}%" : "Unknown",
                resetCountdown,
                "Paused while stale",
                "Forecast paused",
                $"Last known good · {snapshot.Source}",
                "Last-known-good quota is shown; forecasting is paused until the provider is fresh again.",
                InfoBarSeverity.Warning);
        }

        var paceText = forecast?.State switch
        {
            ForecastState.IdleWithinMeterPrecision => "Flat within meter precision",
            _ when forecast?.BurnRatePercentPerHour is double burn && forecast.SustainablePercentPerHour is double sustainable =>
                $"{burn:0.0} pp/h · sustainable {sustainable:0.0} pp/h" +
                (forecast.BurnPressure is double pressure ? $" · {pressure:0.00}× pace" : string.Empty),
            _ when forecast?.SustainablePercentPerHour is double sustainable => $"Learning · sustainable {sustainable:0.0} pp/h",
            _ => "Learning"
        };

        var windowForecast = forecast?.State switch
        {
            ForecastState.ExhaustionLikelyBeforeReset when forecast.EstimatedExhaustionAtUtc is DateTimeOffset exhaustion =>
                $"Exhaustion likely {exhaustion.ToLocalTime():ddd HH:mm}",
            ForecastState.SafeUntilReset or ForecastState.NearSustainablePace when forecast.ProjectedRemainingAtResetPercent is double margin =>
                $"Survives reset · ~{margin:0.#}% remaining at reset",
            ForecastState.IdleWithinMeterPrecision => "No meter movement visible yet",
            _ => "Learning from this reset window"
        };

        var survivalMessage = forecast?.State switch
        {
            ForecastState.ExhaustionLikelyBeforeReset => "Current pace is projected to exhaust this quota window before its authoritative reset.",
            ForecastState.NearSustainablePace => "Current pace is close to the sustainable pace for this reset window.",
            ForecastState.SafeUntilReset => "Current pace is projected to survive the current reset window.",
            ForecastState.IdleWithinMeterPrecision => "Quota has not moved at the provider meter's visible precision; burn is uncertain rather than assumed to be exactly zero.",
            _ => "Quota is live; more observations from this reset window are needed before making a burn claim."
        };

        var severity = forecast?.State switch
        {
            ForecastState.ExhaustionLikelyBeforeReset => InfoBarSeverity.Warning,
            ForecastState.SafeUntilReset => InfoBarSeverity.Success,
            ForecastState.NearSustainablePace => InfoBarSeverity.Informational,
            _ => InfoBarSeverity.Informational
        };

        var confidence = forecast is null ? string.Empty : $" · confidence {forecast.Confidence:P0}";
        var trend = string.IsNullOrWhiteSpace(forecast?.Trend) ? string.Empty : $" · {forecast.Trend}";
        var freshness = $"live · {snapshot.Source}";

        return new QuotaCardViewModel(
            title,
            remaining is double value ? $"{value:0.#}%" : "Unknown",
            resetCountdown,
            paceText,
            windowForecast,
            $"{freshness}{confidence}{trend}",
            survivalMessage,
            severity);
    }

    private static QuotaCardViewModel UnavailableQuota(string title, string detail) =>
        new(title, "Unavailable", "Unknown", "Unavailable", "Unavailable", "No quota data", detail, InfoBarSeverity.Warning);

    private void AddEvent(string type, string description) =>
        RecentEvents.Insert(0, new EventItem("Now", type, description));

    private static string FormatHealth(TelemetryHealthState state) => state switch
    {
        TelemetryHealthState.Live => "Live",
        TelemetryHealthState.Stale => "Stale",
        TelemetryHealthState.Unavailable => "Unavailable",
        TelemetryHealthState.Error => "Error",
        _ => state.ToString()
    };

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
