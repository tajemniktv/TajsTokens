using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.App.Pages;

public sealed partial class CodexStateDbExplorerPage : Page
{
    private readonly HashSet<CodexStateRawRow> _selectedRawRows = [];
    private CancellationTokenSource? _cancellation;
    private CodexStateInspectionResult? _inspection;
    private CodexStateRawPage? _page;
    private CodexStateInspectionSnapshot? _baseline;
    private string? _selectedDatabasePath;
    private int _pageIndex;
    private bool _loaded;
    private bool _suppressDatabaseSelection;
    private long _loadGeneration;

    public CodexStateDbExplorerPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private App App => (App)Application.Current;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        ReplaceCancellation();
        await RefreshInspectionAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        ReplaceCancellation(cancelOnly: true);
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await RefreshInspectionAsync();

    private async Task RefreshInspectionAsync()
    {
        if (!_loaded)
        {
            return;
        }

        var cancellation = _cancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _loadGeneration);
        try
        {
            StatusText.Text = "Discovering Codex state databases…";
            var candidates = await Task.Run(
                () => App.Services.CodexStateExplorer.DiscoverCandidates(),
                cancellation.Token);
            if (!CanApply(generation, cancellation))
            {
                return;
            }

            var selected = candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.Path, _selectedDatabasePath, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault();
            _suppressDatabaseSelection = true;
            try
            {
                DatabaseComboBox.ItemsSource = candidates;
                DatabaseComboBox.SelectedItem = selected;
            }
            finally
            {
                _suppressDatabaseSelection = false;
            }
            if (selected is null)
            {
                _inspection = null;
                _page = null;
                ClearInspection("No Codex SQLite files were discovered under the configured CODEX_HOME, its sqlite folder, or the snapshot folder.");
                return;
            }

            _selectedDatabasePath = selected.Path;
            DatabasePathText.Text = selected.Path;
            StatusText.Text = $"Opening {selected.FileName} read-only with SQLite query_only…";
            var inspection = await Task.Run(
                () => App.Services.CodexStateExplorer.InspectAsync(selected.Path, cancellation.Token),
                cancellation.Token);
            if (!CanApply(generation, cancellation))
            {
                return;
            }

            ApplyInspection(inspection);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Page navigation or a newer refresh owns cancellation.
        }
        catch (Exception exception)
        {
            if (CanApply(generation, cancellation))
            {
                StatusText.Text = $"State DB inspection unavailable: {Summarize(exception.Message)}";
            }
        }
    }

    private async void OnDatabaseSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDatabaseSelection || !_loaded || DatabaseComboBox.SelectedItem is not CodexStateDatabaseCandidate candidate)
        {
            return;
        }

        _selectedDatabasePath = candidate.Path;
        DatabasePathText.Text = candidate.Path;
        await InspectSelectedDatabaseAsync(candidate.Path);
    }

    private async Task InspectSelectedDatabaseAsync(string databasePath)
    {
        var cancellation = _cancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _loadGeneration);
        try
        {
            StatusText.Text = $"Opening {Path.GetFileName(databasePath)} read-only with SQLite query_only…";
            var inspection = await Task.Run(
                () => App.Services.CodexStateExplorer.InspectAsync(databasePath, cancellation.Token),
                cancellation.Token);
            if (CanApply(generation, cancellation))
            {
                ApplyInspection(inspection);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (CanApply(generation, cancellation))
            {
                StatusText.Text = $"State DB inspection unavailable: {Summarize(exception.Message)}";
            }
        }
    }

    private async void OnTableSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TableList.SelectedItem is CodexStateTableInfo table)
        {
            await SelectTableAsync(table);
        }
    }

    private async void OnFocusedTableChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FocusedTableComboBox.SelectedItem is CodexStateTableInfo table)
        {
            TableList.SelectedItem = table;
            await SelectTableAsync(table);
        }
    }

    private async Task SelectTableAsync(CodexStateTableInfo table)
    {
        SelectedTableText.Text = $"{table.Name} ({table.ObjectType})";
        SelectedTableSchemaText.Text = $"{table.RowCount:N0} rows · source SQL is shown below; this is not a normalized product model.";
        ColumnsText.Text = table.Columns.Count == 0
            ? "Columns: (none reported by PRAGMA table_info)"
            : "Columns: " + string.Join(", ", table.Columns.Select(column =>
                $"{column.Name} {column.DeclaredType}{(column.IsPrimaryKey ? " PK" : string.Empty)}"));
        IndexesText.Text = table.Indexes.Count == 0
            ? "Indexes: none"
            : "Indexes: " + string.Join(", ", table.Indexes.Select(index =>
                $"{index.Name} ({string.Join(", ", index.Columns)})"));
        SqlText.Text = string.IsNullOrWhiteSpace(table.Sql) ? "SQL: (not provided)" : table.Sql;

        _pageIndex = 0;
        await LoadPageAsync(table.Name);
    }

    private async Task LoadPageAsync(string tableName)
    {
        var databasePath = _selectedDatabasePath;
        var cancellation = _cancellation;
        if (databasePath is null || cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        var pageIndex = _pageIndex;
        var generation = Interlocked.Increment(ref _loadGeneration);
        try
        {
            var page = await Task.Run(
                () => App.Services.CodexStateExplorer.ReadPageAsync(
                    databasePath,
                    tableName,
                    pageIndex,
                    50,
                    cancellation.Token),
                cancellation.Token);
            if (!CanApply(generation, cancellation))
            {
                return;
            }

            _page = page;
            RenderRawGrid(page);
            PageSummaryText.Text = page.RangeSummary;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (CanApply(generation, cancellation))
            {
                StatusText.Text = $"Table page unavailable: {Summarize(exception.Message)}";
            }
        }
    }

    private void RenderRawGrid(CodexStateRawPage page)
    {
        _selectedRawRows.Clear();
        RawGrid.Children.Clear();
        RawGrid.RowDefinitions.Clear();
        RawGrid.ColumnDefinitions.Clear();
        RawGrid.HorizontalAlignment = HorizontalAlignment.Left;

        RawGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        for (var columnIndex = 0; columnIndex < page.Columns.Count; columnIndex++)
        {
            RawGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(EstimateColumnWidth(page, columnIndex))
            });
        }

        AddGridRow(page, rowIndex: 0, isHeader: true, row: null);
        for (var rowIndex = 0; rowIndex < page.Rows.Count; rowIndex++)
        {
            AddGridRow(page, rowIndex + 1, isHeader: false, page.Rows[rowIndex]);
        }
    }

    private void AddGridRow(
        CodexStateRawPage page,
        int rowIndex,
        bool isHeader,
        CodexStateRawRow? row)
    {
        RawGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (isHeader)
        {
            AddGridCell(CreateCellText("Select", isHeader: true), rowIndex, 0, isHeader: true);
        }
        else
        {
            var selector = new CheckBox
            {
                Tag = row,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4)
            };
            selector.Checked += OnRawRowSelectionChanged;
            selector.Unchecked += OnRawRowSelectionChanged;
            AddGridCell(selector, rowIndex, 0, isHeader: false);
        }

        for (var columnIndex = 0; columnIndex < page.Columns.Count; columnIndex++)
        {
            var value = isHeader
                ? page.Columns[columnIndex]
                : row is not null && columnIndex < row.Values.Count
                    ? CodexStateDbExplorerService.FormatRawValue(row.Values[columnIndex])
                    : string.Empty;
            AddGridCell(CreateCellText(value, isHeader), rowIndex, columnIndex + 1, isHeader);
        }
    }

    private void AddGridCell(FrameworkElement content, int rowIndex, int columnIndex, bool isHeader)
    {
        var border = new Border
        {
            Child = content,
            Padding = new Thickness(10, isHeader ? 8 : 7, 10, isHeader ? 8 : 7),
            BorderThickness = new Thickness(0, 0, 1, 1),
            BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush")
        };
        if (isHeader)
        {
            border.Background = ResolveBrush("CardBackgroundFillColorSecondaryBrush") ??
                                ResolveBrush("CardBackgroundFillColorDefaultBrush");
        }

        Grid.SetRow(border, rowIndex);
        Grid.SetColumn(border, columnIndex);
        RawGrid.Children.Add(border);
    }

    private static TextBlock CreateCellText(string value, bool isHeader)
    {
        var text = new TextBlock
        {
            Text = value,
            FontFamily = new FontFamily("Consolas"),
            FontWeight = isHeader ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(text, value);
        return text;
    }

    private static double EstimateColumnWidth(CodexStateRawPage page, int columnIndex)
    {
        var maxCharacters = page.Columns[columnIndex].Length;
        foreach (var row in page.Rows)
        {
            if (columnIndex < row.Values.Count)
            {
                maxCharacters = Math.Max(
                    maxCharacters,
                    Math.Min(42, CodexStateDbExplorerService.FormatRawValue(row.Values[columnIndex]).Length));
            }
        }

        return Math.Clamp(maxCharacters * 8d + 28d, 120d, 360d);
    }

    private static Brush? ResolveBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var resource) ? resource as Brush : null;

    private async void OnPreviousPageClicked(object sender, RoutedEventArgs e)
    {
        if (_page is null || _pageIndex == 0 || TableList.SelectedItem is not CodexStateTableInfo table)
        {
            return;
        }

        _pageIndex--;
        await LoadPageAsync(table.Name);
    }

    private async void OnNextPageClicked(object sender, RoutedEventArgs e)
    {
        if (_page is null || TableList.SelectedItem is not CodexStateTableInfo table ||
            (_pageIndex + 1L) * _page.PageSize >= _page.TotalRows)
        {
            return;
        }

        _pageIndex++;
        await LoadPageAsync(table.Name);
    }

    private void OnCaptureBaselineClicked(object sender, RoutedEventArgs e)
    {
        if (_inspection is null)
        {
            BaselineText.Text = "Inspect a database before capturing a baseline.";
            return;
        }

        _baseline = _inspection.Snapshot;
        DiffList.ItemsSource = Array.Empty<CodexStateTableDiff>();
        BaselineText.Text = $"Baseline captured {_baseline.CapturedAtUtc.ToLocalTime():g} · {_baseline.Tables.Count:N0} tables. Refresh to compare table schemas and bounded row fingerprints.";
        StatusText.Text = "Baseline captured in memory only; no inspection data was persisted.";
    }

    private void OnRawRowSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not CodexStateRawRow row)
        {
            return;
        }

        if (checkBox.IsChecked == true)
        {
            _selectedRawRows.Add(row);
        }
        else
        {
            _selectedRawRows.Remove(row);
        }
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        var export = BuildSelectedExport();
        if (export is null)
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(export);
            Clipboard.SetContent(package);
            StatusText.Text = "Sanitized inspection data copied to the clipboard.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Copy failed: {Summarize(exception.Message)}";
        }
    }

    private async void OnExportClicked(object sender, RoutedEventArgs e)
    {
        var export = BuildSelectedExport();
        if (export is null)
        {
            return;
        }

        try
        {
            var exportDirectory = Path.Combine(App.Services.DataFolder, "InspectionExports");
            Directory.CreateDirectory(exportDirectory);
            var table = _page?.TableName ?? "table";
            var fileName = $"codex-state-{SanitizeFileName(table)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.tsv";
            var path = Path.Combine(exportDirectory, fileName);
            await File.WriteAllTextAsync(path, export, Encoding.UTF8, _cancellation?.Token ?? default);
            StatusText.Text = $"Sanitized inspection export written to {path}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Export failed: {Summarize(exception.Message)}";
        }
    }

    private async void OnExportCombinedSchemaClicked(object sender, RoutedEventArgs e)
    {
        var cancellation = _cancellation;
        if (!_loaded || cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            StatusText.Text = "Reading schemas from all discovered Codex databases…";
            var candidates = await Task.Run(
                () => App.Services.CodexStateExplorer.DiscoverCandidates(),
                cancellation.Token);
            var inspections = new List<CodexStateInspectionResult>(candidates.Count);
            var failures = new List<string>();
            foreach (var candidate in candidates)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    // Schema export does not need row fingerprints. This keeps large content-heavy
                    // snapshots useful without scanning their bodies.
                    inspections.Add(await Task.Run(
                        () => App.Services.CodexStateExplorer.InspectAsync(
                            candidate.Path,
                            includeRowFingerprints: false,
                            cancellationToken: cancellation.Token),
                        cancellation.Token));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Add($"{candidate.Path}: {Summarize(exception.Message)}");
                }
            }

            var schema = new StringBuilder(CodexStateDbExplorerService.BuildCombinedSchemaExport(inspections));
            if (failures.Count > 0)
            {
                schema.AppendLine("-- UNAVAILABLE SOURCES");
                foreach (var failure in failures)
                {
                    schema.Append("-- ").AppendLine(failure);
                }
            }

            var exportDirectory = Path.Combine(App.Services.DataFolder, "InspectionExports");
            Directory.CreateDirectory(exportDirectory);
            var path = Path.Combine(
                exportDirectory,
                $"codex-combined-schema-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.sql");
            await File.WriteAllTextAsync(path, schema.ToString(), Encoding.UTF8, cancellation.Token);
            StatusText.Text = $"Combined schema exported to {path} · {inspections.Count:N0}/{candidates.Count:N0} databases read.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Combined schema export failed: {Summarize(exception.Message)}";
        }
    }

    private string? BuildSelectedExport()
    {
        if (_page is null)
        {
            StatusText.Text = "Select a table page before copying or exporting.";
            return null;
        }

        var selected = _page.Rows.Where(_selectedRawRows.Contains).ToArray();
        if (selected.Length == 0)
        {
            return CodexStateDbExplorerService.BuildSanitizedExport(_page);
        }

        var selectedPage = _page with { Rows = selected };
        return CodexStateDbExplorerService.BuildSanitizedExport(selectedPage);
    }

    private void ApplyInspection(CodexStateInspectionResult inspection)
    {
        _inspection = inspection;
        var selectedTableName = (TableList.SelectedItem as CodexStateTableInfo)?.Name;
        TableList.ItemsSource = inspection.Tables;
        FocusedTableComboBox.ItemsSource = inspection.FocusedTables;
        TableCountText.Text = $"{inspection.Tables.Count:N0} objects · {inspection.Database.FileName}";
        DatabasePathText.Text = inspection.Database.Path;

        var selectedTable = inspection.Tables.FirstOrDefault(table =>
            string.Equals(table.Name, selectedTableName, StringComparison.OrdinalIgnoreCase))
            ?? inspection.FocusedTables.FirstOrDefault()
            ?? inspection.Tables.FirstOrDefault();
        if (selectedTable is not null)
        {
            TableList.SelectedItem = selectedTable;
        }
        else
        {
            ClearTableDetail();
        }

        if (_baseline is not null &&
            string.Equals(_baseline.DatabasePath, inspection.Database.Path, StringComparison.OrdinalIgnoreCase))
        {
            var diff = CodexStateDbExplorerService.Compare(_baseline, inspection.Snapshot);
            DiffList.ItemsSource = diff.Tables;
            BaselineText.Text = diff.HasChanges
                ? $"Compared with baseline from {diff.BaselineCapturedAtUtc.ToLocalTime():g}: {diff.Tables.Count:N0} changed/added/removed table(s)."
                : $"Compared with baseline from {diff.BaselineCapturedAtUtc.ToLocalTime():g}: no table or row changes observed.";
        }
        else if (_baseline is not null)
        {
            DiffList.ItemsSource = Array.Empty<CodexStateTableDiff>();
            BaselineText.Text = $"A baseline exists for {Path.GetFileName(_baseline.DatabasePath)}, not this selected file.";
        }

        StatusText.Text = $"Inspecting {inspection.Database.Path} · {inspection.Tables.Count:N0} tables/views · read-only/query_only · refreshed {inspection.Snapshot.CapturedAtUtc.ToLocalTime():g}.";
    }

    private void ClearInspection(string message)
    {
        DatabasePathText.Text = "No database selected";
        TableCountText.Text = "0 objects";
        TableList.ItemsSource = Array.Empty<CodexStateTableInfo>();
        FocusedTableComboBox.ItemsSource = Array.Empty<CodexStateTableInfo>();
        DiffList.ItemsSource = Array.Empty<CodexStateTableDiff>();
        ClearTableDetail();
        StatusText.Text = message;
    }

    private void ClearTableDetail()
    {
        SelectedTableText.Text = "Select a table or view";
        SelectedTableSchemaText.Text = "Schema, columns, indexes and raw rows appear here.";
        ColumnsText.Text = "—";
        IndexesText.Text = "—";
        SqlText.Text = "—";
        PageSummaryText.Text = "No rows loaded";
        _page = null;
        _selectedRawRows.Clear();
        RawGrid.Children.Clear();
        RawGrid.RowDefinitions.Clear();
        RawGrid.ColumnDefinitions.Clear();
        }

    private bool CanApply(long generation, CancellationTokenSource cancellation) =>
        _loaded && ReferenceEquals(_cancellation, cancellation) &&
        !cancellation.IsCancellationRequested && generation == Volatile.Read(ref _loadGeneration);

    private void ReplaceCancellation(bool cancelOnly = false)
    {
        var previous = Interlocked.Exchange(ref _cancellation, cancelOnly ? null : new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.Length == 0 ? "table" : builder.ToString();
    }

    private static string Summarize(string message) => message.ReplaceLineEndings(" ").Trim();
}
