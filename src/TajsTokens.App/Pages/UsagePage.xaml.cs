using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.App.Pages;

public sealed partial class UsagePage : Page
{
    public event EventHandler<string>? ThreadRequested;
    public void SelectThread(string threadId)
    {
        _hasResult = false;
        _model = _repository = null;
        _session = null;
        _thread = threadId;
        _from = _to = null;
        // Called while detached, before Loaded starts the scoped query.
        RangeCombo.SelectedIndex = 4;
        ViewCombo.SelectedIndex = 4;
        SearchBox.Text = "";
    }
    private bool _loaded;
    private bool _hasResult;
    private bool _settingRange;
    private CancellationTokenSource? _request;
    private IReadOnlyList<UsageRow> _rows = [];
    private string? _model, _repository, _session, _thread;
    private DateTimeOffset? _from, _to;
    private string _group = "Model";
    private IntelligenceQuery? _displayedQuery;

    public UsagePage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { _loaded = true; if (!_hasResult) await LoadAsync(); };
        Unloaded += (_, _) => { _loaded = false; _request?.Cancel(); };
    }

    private async Task LoadAsync()
    {
        if (!_loaded) return;
        _hasResult = false;
        _request?.Cancel();
        var request = new CancellationTokenSource();
        _request = request;
        RowsList.ItemsSource = null;
        _rows = [];
        SummaryText.Text = DetailText.Text = "";
        DrillButton.IsEnabled = false;
        OpenThreadButton.IsEnabled = false;
        StatusText.Text = "Reading local history…";
        try
        {
            _group = ((ComboBoxItem)ViewCombo.SelectedItem).Tag.ToString()!;
            var range = ((ComboBoxItem)RangeCombo.SelectedItem).Tag.ToString();
            var now = DateTimeOffset.UtcNow;
            var from = _from ?? (range == "all" ? DateTimeOffset.UnixEpoch : now.AddDays(-int.Parse(range!)));
            var size = Enum.TryParse<AnalyticsBucketSize>(_group, out var bucket) ? bucket : AnalyticsBucketSize.Month;
            var query = new IntelligenceQuery(from, _to ?? now, size, 2000)
            { UsageOnly = true, Model = _model, Repository = _repository, SessionId = _session, ThreadId = _thread };
            var service = ((App)Application.Current).Services.Intelligence;
            var data = await Task.Run(() => service.QueryAsync(query, request.Token), request.Token);
            if (!_loaded || _request != request || request.IsCancellationRequested) return;
            _displayedQuery = data.Query;
            var time = _group is "Hour" or "Day" or "Month";
            _rows = time
                ? data.UsageHistory.Select(x => new UsageRow(x.StartUtc.ToString(data.Query.BucketSize == AnalyticsBucketSize.Month ? "yyyy-MM" : data.Query.BucketSize == AnalyticsBucketSize.Day ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm"), x.NativeTokens, x.UncachedInputTokens, x.NonReasoningOutputTokens, x.CacheReadTokens, x.CacheWriteTokens, x.ReasoningOutputTokens, x.ActiveSessions, x.StartUtc, x.EndUtc)).ToArray()
                : data.Dimensions.Where(x => x.Dimension == _group).Select(x => new UsageRow(x.Value, x.NativeTokens, x.UncachedInputTokens, x.NonReasoningOutputTokens, x.CacheReadTokens, x.CacheWriteTokens, x.ReasoningOutputTokens, x.Sessions, ThreadId: x.ThreadId)).ToArray();
            SummaryText.Text = $"{Count(data.UsageHistory.Sum(x => x.NativeTokens))} recorded tokens · {data.Dimensions.Count(x => x.Dimension == "Session"):N0} sessions";
            var filters = string.Join(" · ", new[] { _model, _repository, _session, _thread is null ? null : $"Thread {_thread}" }.Where(x => x is not null));
            StatusText.Text = $"{data.Query.FromUtc:yyyy-MM-dd HH:mm} → {data.Query.ToUtc:yyyy-MM-dd HH:mm} UTC · {_rows.Count:N0} rows" +
                (time ? $" · {data.Query.BucketSize} buckets (coarsened when necessary)" : "") +
                (filters.Length > 0 ? $" · {filters}" : " · All local Codex activity") +
                (_rows.Count == 0 ? " · No recorded activity in this scope." : "");
            RenderRows();
            _hasResult = true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (_loaded && _request == request)
            {
                var detail = e.GetBaseException().Message.ReplaceLineEndings(" ").Trim();
                StatusText.Text = "Usage unavailable: " + (detail.Length > 260 ? detail[..260] + "…" : detail);
            }
        }
        finally { if (_request == request) _request = null; request.Dispose(); }
    }

    private void RenderRows()
    {
        if (!_loaded) return;
        var rows = _rows.Where(x => x.Label.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase));
        RowsList.ItemsSource = (SortCombo.SelectedIndex == 0 ? rows.OrderByDescending(x => x.Total) : rows.OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase)).ToArray();
        DrillButton.IsEnabled = false;
        DetailText.Text = "";
    }
    private async void OnQueryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingRange) return;
        _hasResult = false;
        if (ReferenceEquals(sender, RangeCombo)) _from = _to = null;
        await LoadAsync();
    }
    private void OnViewChanged(object sender, SelectionChangedEventArgs e) => RenderRows();
    private void OnSearchChanged(object sender, TextChangedEventArgs e) => RenderRows();
    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await LoadAsync();
    private async void OnClearClicked(object sender, RoutedEventArgs e)
    {
        _model = _repository = _session = _thread = null; _from = _to = null;
        _settingRange = true;
        if (RangeCombo.SelectedIndex == 5) RangeCombo.SelectedIndex = 2;
        _settingRange = false;
        SearchBox.Text = ""; await LoadAsync();
    }
    private void OnRowSelected(object sender, SelectionChangedEventArgs e)
    {
        var row = RowsList.SelectedItem as UsageRow;
        DrillButton.IsEnabled = row is not null;
        OpenThreadButton.IsEnabled = row?.ThreadId is not null && _group == "Session";
        DetailText.Text = row is null ? "" : $"{row.Label}\nInput {row.Input:N0} · output {row.Output:N0} · cache read {row.Cache:N0} · cache write {row.Write:N0} · reasoning {row.Reasoning:N0} · total {row.Total:N0}" +
            $" · reported minus bucket sum: {row.Total - row.Input - row.Output - row.Cache - row.Write - row.Reasoning:N0}";
    }
    private async void OnDrillClicked(object sender, RoutedEventArgs e)
    {
        if (RowsList.SelectedItem is not UsageRow row || _displayedQuery is null) return;
        if (_group == "Model") _model = row.Label;
        else if (_group == "Session repository") _repository = row.Label;
        else if (_group == "Session") _session = row.Label;
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
    private static string Count(long n) => n switch { >= 1_000_000_000 => $"{n / 1e9:0.00}B", >= 1_000_000 => $"{n / 1e6:0.00}M", >= 1000 => $"{n / 1e3:0.0}K", _ => n.ToString("N0") };
    private sealed record UsageRow(string Label, long Total, long Input, long Output, long Cache, long Write, long Reasoning, int Sessions, DateTimeOffset? Start = null, DateTimeOffset? End = null, string? ThreadId = null)
    {
        public string InputText => Count(Input);
        public string OutputText => Count(Output);
        public string CacheText => Count(Cache);
        public string WriteText => Count(Write);
        public string ReasoningText => Count(Reasoning);
        public string TotalText => Count(Total);
    }
}
