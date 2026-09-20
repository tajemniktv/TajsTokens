// Taj's Tokens | UsagePage.xaml.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.App.Pages;

public sealed partial class UsagePage : Page
{
    private readonly DataTemplate? _detailedTemplate;
    private IntelligenceQuery? _displayedQuery;
    private DateTimeOffset? _from, _to;
    private string _group = "Model";
    private bool _loaded;
    private string? _model, _repository, _session, _thread;
    private CancellationTokenSource? _request;
    private IReadOnlyList<UsageRow> _rows = [];
    private bool _settingRange;
    private IReadOnlyList<UsageHistoryBucket> _timeline = [];

    public UsagePage()
    {
        InitializeComponent();
        _detailedTemplate = RowsList.ItemTemplate;
        OnColumnsChanged(this, new RoutedEventArgs());
        Loaded += async (_, _) =>
        {
            _loaded = true;
            await LoadAsync();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            _request?.Cancel();
        };
    }

    public event EventHandler<string>? ThreadRequested;

    public void SelectThread(string threadId)
    {
        _model = _repository = null;
        _session = null;
        _thread = threadId;
        _from = _to = null;
        // Called while detached, before Loaded starts the scoped query.
        RangeCombo.SelectedIndex = 4;
        ViewCombo.SelectedIndex = 4;
        SearchBox.Text = "";
    }

    private async Task LoadAsync()
    {
        if (!_loaded) return;
        _request?.Cancel();
        var request = new CancellationTokenSource();
        _request = request;
        RowsList.ItemsSource = null;
        _rows = [];
        _timeline = [];
        UsageTimeline.Children.Clear();
        TimelineCaption.Text = "Reading recorded activity…";
        SummaryText.Text = DetailText.Text = "";
        DrillButton.IsEnabled = false;
        OpenThreadButton.IsEnabled = false;
        StatusText.Text = "Reading local history…";
        try
        {
            _group = ((ComboBoxItem)ViewCombo.SelectedItem).Tag.ToString()!;
            string? range = ((ComboBoxItem)RangeCombo.SelectedItem).Tag.ToString();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset from = _from ?? (range == "all" ? DateTimeOffset.UnixEpoch : now.AddDays(-int.Parse(range!)));
            AnalyticsBucketSize size = Enum.TryParse<AnalyticsBucketSize>(_group, out AnalyticsBucketSize bucket) ? bucket :
                (now - from).TotalDays <= 2 ? AnalyticsBucketSize.Hour : AnalyticsBucketSize.Day;
            var query = new IntelligenceQuery(from, _to ?? now, size, 2000)
            {
                UsageOnly = true,
                Model = _model,
                Repository = _repository,
                SessionId = _session,
                ThreadId = _thread,
            };
            IIntelligenceService service = ((App)Application.Current).Services.Intelligence;
            IntelligenceDashboard data = await Task.Run(() => service.QueryAsync(query, request.Token), request.Token);
            if (!_loaded || _request != request || request.IsCancellationRequested) return;
            _displayedQuery = data.Query;
            _timeline = data.UsageHistory;
            bool time = _group is "Hour" or "Day" or "Month";
            _rows = time
                ? data.UsageHistory.Select(x => new UsageRow(
                    $"{x.StartUtc.ToLocalTime():dd MMM HH:mm} → {x.EndUtc.ToLocalTime():dd MMM HH:mm}",
                    x.NativeTokens,
                    x.UncachedInputTokens,
                    x.NonReasoningOutputTokens,
                    x.CacheReadTokens,
                    x.CacheWriteTokens,
                    x.ReasoningOutputTokens,
                    x.ActiveSessions,
                    x.StartUtc,
                    x.EndUtc)).ToArray()
                : data.Dimensions.Where(x => x.Dimension == _group).Select(x => new UsageRow(
                    x.Value,
                    x.NativeTokens,
                    x.UncachedInputTokens,
                    x.NonReasoningOutputTokens,
                    x.CacheReadTokens,
                    x.CacheWriteTokens,
                    x.ReasoningOutputTokens,
                    x.Sessions,
                    ThreadId: x.ThreadId)).ToArray();
            SummaryText.Text = $"{Count(data.UsageHistory.Sum(x => x.NativeTokens))} tokens";
            PeriodText.Text =
                $"{data.Query.FromUtc.ToLocalTime():d MMM yyyy} – {data.Query.ToUtc.ToLocalTime():d MMM yyyy} · this computer";
            StatusText.Text =
                $"{data.Query.FromUtc.ToLocalTime():g} → {data.Query.ToUtc.ToLocalTime():g} · {TimeZoneInfo.Local.DisplayName} · {_rows.Count:N0} rows" +
                (time ? $" · {data.Query.BucketSize} buckets (coarsened when necessary)" : "") +
                (_rows.Count == 0 ? " · No recorded activity in this scope." : "");
            RenderChips();
            RenderTimeline();
            RenderRows();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (_loaded && _request == request)
            {
                string detail = e.GetBaseException().Message.ReplaceLineEndings(" ").Trim();
                StatusText.Text = "Usage unavailable: " + (detail.Length > 260 ? detail[..260] + "…" : detail);
            }
        }
        finally
        {
            if (_request == request) _request = null;
            request.Dispose();
        }
    }

    private void RenderRows()
    {
        if (!_loaded) return;
        IEnumerable<UsageRow> rows = _rows.Where(x => x.Label.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase));
        RowsList.ItemsSource = (SortCombo.SelectedIndex == 0
            ? rows.OrderByDescending(x => x.Total)
            : rows.OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase)).ToArray();
        DrillButton.IsEnabled = false;
        DetailText.Text = "";
    }

    private void OnColumnsChanged(object sender, RoutedEventArgs e)
    {
        if (RowsList is null || _detailedTemplate is null) return;
        bool detailed = DetailedColumns.IsChecked == true;
        RowsList.ItemTemplate = detailed ? _detailedTemplate : (DataTemplate)Resources["CompactUsageRow"];
        DetailedHeader.Visibility = detailed ? Visibility.Visible : Visibility.Collapsed;
        CompactHeader.Visibility = detailed ? Visibility.Collapsed : Visibility.Visible;
        UsageTable.MinWidth = detailed ? 1040 : 0;
    }

    private void RenderChips()
    {
        FilterChips.Children.Clear();
        FilterChips.Children.Add(new TextBlock { Text = "All usage", VerticalAlignment = VerticalAlignment.Center });

        void Add(string label, Action clear)
        {
            var button = new Button { Content = label + " ×", MaxWidth = 300 };
            ToolTipService.SetToolTip(button, "Remove filter: " + label);
            button.Click += async (_, _) =>
            {
                clear();
                await LoadAsync();
            };
            FilterChips.Children.Add(button);
        }

        if (_model is not null) Add("Model: " + _model, () => _model = null);
        if (_repository is not null) Add("Project: " + _repository, () => _repository = null);
        if (_session is not null) Add("Session: " + _session, () => _session = null);
        if (_thread is not null) Add("Thread: " + _thread, () => _thread = null);
        if (_from is not null)
            Add(
                "Selected period",
                () =>
                {
                    _from = _to = null;
                    _settingRange = true;
                    RangeCombo.SelectedIndex = 2;
                    _settingRange = false;
                });
    }

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderTimeline();
    }

    private void RenderTimeline()
    {
        UsageTimeline.Children.Clear();
        if (_timeline.Count == 0 || UsageTimeline.ActualWidth <= 0 || _displayedQuery is null) return;
        DateTimeOffset from = _displayedQuery.FromUtc == DateTimeOffset.UnixEpoch
            ? _timeline.Min(x => x.StartUtc)
            : _displayedQuery.FromUtc;
        DateTimeOffset to = _displayedQuery.ToUtc;
        double span = Math.Max(1, (to - from).TotalSeconds);
        // Coarsen only the visual bars, never the totals or drill-down query.
        var bins = _timeline.GroupBy(x => Math.Clamp((int)((x.StartUtc - from).TotalSeconds / span * 60), 0, 59))
            .Select(g => new
            {
                Index = g.Key, Tokens = g.Sum(x => x.NativeTokens), From = g.Min(x => x.StartUtc), To = g.Max(x => x.EndUtc),
            }).ToArray();
        long max = Math.Max(1, bins.Max(x => x.Tokens));
        foreach (var bin in bins)
        {
            var bar = new Border
            {
                Width = Math.Max(1, UsageTimeline.ActualWidth / 60 - 2),
                Height = 80d * bin.Tokens / max,
                Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(3, 3, 0, 0),
            };
            Canvas.SetLeft(bar, bin.Index * UsageTimeline.ActualWidth / 60);
            Canvas.SetTop(bar, 90 - bar.Height);
            ToolTipService.SetToolTip(bar, $"{bin.From.ToLocalTime():g} → {bin.To.ToLocalTime():g}: {bin.Tokens:N0} recorded tokens");
            UsageTimeline.Children.Add(bar);
        }
        TimelineCaption.Text =
            $"{from.ToLocalTime():d MMM HH:mm} → {to.ToLocalTime():d MMM HH:mm} · recorded activity; gaps preserved · local time";
    }

    private async void OnQueryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingRange) return;
        if (ReferenceEquals(sender, RangeCombo)) _from = _to = null;
        await LoadAsync();
    }

    private void OnViewChanged(object sender, SelectionChangedEventArgs e)
    {
        RenderRows();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        RenderRows();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async void OnClearClicked(object sender, RoutedEventArgs e)
    {
        _model = _repository = _session = _thread = null;
        _from = _to = null;
        _settingRange = true;
        if (RangeCombo.SelectedIndex == 5) RangeCombo.SelectedIndex = 2;
        _settingRange = false;
        SearchBox.Text = "";
        await LoadAsync();
    }

    private void OnRowSelected(object sender, SelectionChangedEventArgs e)
    {
        var row = RowsList.SelectedItem as UsageRow;
        DrillButton.IsEnabled = row is not null;
        OpenThreadButton.IsEnabled = row?.ThreadId is not null && _group == "Session";
        DetailText.Text = row is null
            ? ""
            : $"{row.Label}\nInput {row.Input:N0} · output {row.Output:N0} · cache read {row.Cache:N0} · cache write {row.Write:N0} · reasoning {row.Reasoning:N0} · total {row.Total:N0}" +
              $" · reported minus bucket sum: {row.Total - row.Input - row.Output - row.Cache - row.Write - row.Reasoning:N0}";
    }

    private async void OnDrillClicked(object sender, RoutedEventArgs e)
    {
        if (RowsList.SelectedItem is not UsageRow row || _displayedQuery is null) return;
        if (_group == "Model")
        {
            _model = row.Label;
        }
        else if (_group == "Session repository")
        {
            _repository = row.Label;
        }
        else if (_group == "Session")
        {
            _session = row.Label;
        }
        else if (row.Start is { } start && row.End is { } end)
        {
            _from = start > _displayedQuery.FromUtc ? start : _displayedQuery.FromUtc;
            _to = end < _displayedQuery.ToUtc ? end : _displayedQuery.ToUtc;
            _settingRange = true;
            RangeCombo.SelectedIndex = 5;
            _settingRange = false;
        }
        SearchBox.Text = "";
        await LoadAsync();
    }

    private void OnOpenThreadClicked(object sender, RoutedEventArgs e)
    {
        if (_group == "Session" && RowsList.SelectedItem is UsageRow { ThreadId: { } threadId })
            ThreadRequested?.Invoke(this, threadId);
    }

    private static string Count(long n)
    {
        return n switch
        {
            >= 1_000_000_000 => $"{n / 1e9:0.00}B", >= 1_000_000 => $"{n / 1e6:0.00}M", >= 1000 => $"{n / 1e3:0.0}K",
            _ => n.ToString("N0"),
        };
    }

    private sealed record UsageRow(
        string Label,
        long Total,
        long Input,
        long Output,
        long Cache,
        long Write,
        long Reasoning,
        int Sessions,
        DateTimeOffset? Start = null,
        DateTimeOffset? End = null,
        string? ThreadId = null)
    {
        public string InputText => Count(Input);
        public string OutputText => Count(Output);
        public string CacheText => Count(Cache);
        public string WriteText => Count(Write);
        public string ReasoningText => Count(Reasoning);
        public string TotalText => Count(Total);
    }
}