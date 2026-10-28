// Taj's Tokens | MainWindow.xaml.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Diagnostics;
using System.Text.Json;
using Windows.Graphics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using TajsTokens.App.Pages;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.App;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _saveWindowTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly string _windowStatePath = Path.Combine(((App)Application.Current).Services.DataFolder, "window-state.json");
    private bool _synchronizing;
    private bool _ready;
    private bool _dashboardVisible;
    private long _viewGeneration;
    private Frame? _contentFrame;
    private Frame ContentFrame => _contentFrame ?? throw new InvalidOperationException("Dashboard is unloaded.");
    private readonly Dictionary<Type, object> _pageInputs = [];
    private readonly List<WeakReference<Page>> _pages = [];
    private (Type Type, object? Parameter)? _suspendedPage;
    private (Type Type, object? Parameter)[] _backStack = [], _forwardStack = [];
    private readonly DispatcherTimer _historyEvictionTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly List<WeakReference<IHistoryPresentation>> _historyPages = [];
    private int _historyReleasePasses;
    private WorkMeasurement? _pendingFirstFrame;
    private EventHandler<object>? _firstFrameHandler;
    public string PresentationDiagnostic
    {
        get
        {
            CodexIntelligencePage? overview = _historyPages.Select(reference =>
                reference.TryGetTarget(out var page) ? page : null).OfType<CodexIntelligencePage>().FirstOrDefault();
            return $"Presentation: {(_dashboardVisible ? "active" : _contentFrame is null ? "unloaded" : "paused")}; {_historyReleasePasses} hidden dashboard release pass(es)." +
                (overview is null ? "" : $" Overview: {overview.HistoryReadCount} history read(s), {overview.HistoryCacheHits} qualified cache hit(s).");
        }
    }
    private WindowSizeState _windowSize = new(1200, 850, false);

    public MainWindow()
    {
        InitializeComponent();
        _contentFrame = (Frame)AppNavigation.Content;
        _historyEvictionTimer.Tick += (_, _) =>
        {
            _historyEvictionTimer.Stop();
            if (_dashboardVisible) return;
            if (_contentFrame is not { } frame) return;
            _pages.RemoveAll(reference => !reference.TryGetTarget(out _));
            foreach (var reference in _pages)
                if (reference.TryGetTarget(out var page) && page is IDashboardPresentation presentation)
                    _pageInputs[page.GetType()] = presentation.CaptureInputs();
            if (frame.Content is Page current && current is IDashboardPresentation currentPresentation)
                _pageInputs[current.GetType()] = currentPresentation.CaptureInputs();
            _suspendedPage = frame.CurrentSourcePageType is { } type ? (type, frame.Tag) : null;
            _backStack = frame.BackStack.Select(entry => (entry.SourcePageType, (object?)entry.Parameter)).ToArray();
            _forwardStack = frame.ForwardStack.Select(entry => (entry.SourcePageType, (object?)entry.Parameter)).ToArray();
            foreach (var reference in _historyPages)
                if (reference.TryGetTarget(out var page)) page.ReleaseHistory();
            frame.Navigated -= OnNavigated;
            frame.CacheSize = 0;
            frame.Content = null;
            frame.BackStack.Clear();
            frame.ForwardStack.Clear();
            _contentFrame = null;
            _pages.Clear();
            _historyPages.Clear();
            _historyReleasePasses++;
            WorkDiagnostics.Log.CacheInvalidated((int)WorkPhase.DashboardFirstFrame, 3);
        };
        Closed += (_, _) => { _historyEvictionTimer.Stop(); CancelFirstFrameMeasurement(); };
        try
        {
            if (File.Exists(_windowStatePath))
                _windowSize = JsonSerializer.Deserialize<WindowSizeState>(File.ReadAllText(_windowStatePath)) ?? _windowSize;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        RectInt32 area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        _windowSize = _windowSize with
        {
            Width = Math.Clamp(_windowSize.Width, Math.Min(700, area.Width), area.Width),
            Height = Math.Clamp(_windowSize.Height, Math.Min(650, area.Height), area.Height),
        };
        AppWindow.Resize(new SizeInt32(_windowSize.Width, _windowSize.Height));
        if (_windowSize.Maximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        _saveWindowTimer.Tick += (_, _) => SaveWindowSize();
        AppWindow.Closing += (_, _) => SaveWindowSize();
        Closed += (_, _) => SaveWindowSize();
        AppWindow.Changed += (_, args) =>
        {
            if (!args.DidSizeChange && !args.DidPresenterChange || AppWindow.Presenter is not OverlappedPresenter p ||
                p.State == OverlappedPresenterState.Minimized) return;
            if (p.State == OverlappedPresenterState.Restored)
            {
                SizeInt32 size = AppWindow.Size;
                if (size.Width <= 0 || size.Height <= 0) return;
                if (size.Width < 700 || size.Height < 650)
                {
                    AppWindow.Resize(new SizeInt32(Math.Max(700, size.Width), Math.Max(650, size.Height)));
                    return;
                }
                _windowSize = new WindowSizeState(size.Width, size.Height, false);
            }
            else
            {
                _windowSize = _windowSize with { Maximized = true };
            }
            _saveWindowTimer.Stop();
            _saveWindowTimer.Start();
        };
        _ready = true;
    }

    private void SaveWindowSize()
    {
        _saveWindowTimer.Stop();
        try
        {
            File.WriteAllText(_windowStatePath + ".tmp", JsonSerializer.Serialize(_windowSize));
            File.Move(_windowStatePath + ".tmp", _windowStatePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine("Window size could not be saved: " + e.GetType().Name);
        }
    }

    public void Navigate(Type pageType, object? parameter = null)
    {
        EnsureDashboard();
        if (ContentFrame.CurrentSourcePageType == pageType && Equals(ContentFrame.Tag, parameter)) return;
        ContentFrame.Tag = parameter;
        ContentFrame.Navigate(pageType, parameter);
    }

    public void SetDashboardVisible(bool visible)
    {
        if (_dashboardVisible == visible) return;
        CancelFirstFrameMeasurement();
        _dashboardVisible = visible;
        long generation = ++_viewGeneration;
        if (!visible)
        {
            // Detaching runs existing Unloaded cancellation/subscription cleanup immediately.
            // Brief hides reuse the view; the timer later retains only inputs/navigation.
            AppNavigation.Content = null;
            _historyEvictionTimer.Start();
        }
        else if (visible)
        {
            _historyEvictionTimer.Stop();
            WorkMeasurement? firstFrame = WorkDiagnostics.Measure(WorkPhase.DashboardFirstFrame);
            _pendingFirstFrame = firstFrame;
            // Show/activate the shell first; history queries and page construction follow on a
            // later dispatcher turn and never hold the native tray callback open.
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_dashboardVisible || generation != _viewGeneration) return;
                EnsureDashboard();
                AppNavigation.Content = ContentFrame;
                if (ContentFrame.CurrentSourcePageType is null) Navigate(typeof(CodexIntelligencePage));
                AppNavigation.IsBackEnabled = ContentFrame.CanGoBack;
                if (firstFrame is not null && ReferenceEquals(_pendingFirstFrame, firstFrame))
                {
                    _firstFrameHandler = (_, _) =>
                    {
                        if (!ReferenceEquals(_pendingFirstFrame, firstFrame)) return;
                        if (_dashboardVisible && generation == _viewGeneration) firstFrame.Complete();
                        CancelFirstFrameMeasurement();
                    };
                    CompositionTarget.Rendering += _firstFrameHandler;
                }
            })) CancelFirstFrameMeasurement();
        }
    }

    private void EnsureDashboard()
    {
        if (_contentFrame is not null) return;
        var frame = new Frame { CacheSize = 6 };
        _contentFrame = frame;
        frame.Navigated += OnNavigated;
        if (_suspendedPage is { } current) Navigate(current.Type, current.Parameter);
        foreach (var entry in _backStack) frame.BackStack.Add(new PageStackEntry(entry.Type, entry.Parameter, null));
        foreach (var entry in _forwardStack) frame.ForwardStack.Add(new PageStackEntry(entry.Type, entry.Parameter, null));
        _suspendedPage = null;
        _backStack = _forwardStack = [];
    }

    private void CancelFirstFrameMeasurement()
    {
        if (_firstFrameHandler is not null) CompositionTarget.Rendering -= _firstFrameHandler;
        _firstFrameHandler = null;
        _pendingFirstFrame?.Dispose();
        _pendingFirstFrame = null;
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        if (AppNavigation is null) return;
        _synchronizing = true;
        try
        {
            ContentFrame.Tag = e.Parameter;
            if (e.Content is Page current)
            {
                _pages.RemoveAll(reference => !reference.TryGetTarget(out _));
                if (!_pages.Any(reference => reference.TryGetTarget(out var page) && ReferenceEquals(page, current)))
                    _pages.Add(new(current));
                if (_pageInputs.Remove(current.GetType(), out object? inputs) && current is IDashboardPresentation presentation)
                    presentation.RestoreInputs(inputs);
            }
            _historyPages.RemoveAll(reference => !reference.TryGetTarget(out _));
            if (e.Content is IHistoryPresentation history && !_historyPages.Any(reference =>
                    reference.TryGetTarget(out var page) && ReferenceEquals(page, history)))
                _historyPages.Add(new(history));
            AppNavigation.IsBackEnabled = ContentFrame.CanGoBack;
            string tag = e.SourcePageType == typeof(CodexIntelligencePage) ? Equals(e.Parameter, "planning") ? "session-plan" : "codex-intelligence" :
                e.SourcePageType == typeof(ForecastsPage) && Equals(e.Parameter, "model-lab") ? "model-lab" :
                e.SourcePageType == typeof(ForecastsPage) ? "forecasts" :
                e.SourcePageType == typeof(CodexPage) ? "codex" :
                e.SourcePageType == typeof(AnalyticsPage) ? "analytics" :
                e.SourcePageType == typeof(DiagnosticsPage) ? "diagnostics" :
                e.SourcePageType == typeof(CodexRolloutCoveragePage) ? "coverage" :
                e.SourcePageType == typeof(CodexSourcesPage) ? "sources" :
                e.SourcePageType == typeof(CodexDataExplorerPage) ? "codex-data" :
                e.SourcePageType == typeof(CodexCliHarnessPage) ? "codex-cli" : "settings";
            IEnumerable<NavigationViewItem> items = AppNavigation.MenuItems.OfType<NavigationViewItem>();
            object? selected = tag == "settings"
                ? AppNavigation.SettingsItem
                : items.Concat(items.SelectMany(x => x.MenuItems.OfType<NavigationViewItem>())).FirstOrDefault(x => Equals(x.Tag, tag));
            foreach (NavigationViewItem parent in items.Where(x => x.MenuItems.Contains(selected))) parent.IsExpanded = true;
            AppNavigation.SelectedItem = selected;
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_ready || _synchronizing) return;
        if (args.IsSettingsSelected)
        {
            Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItemContainer?.Tag is not string page)
        {
            return;
        }

        Type pageType = page switch
        {
            "codex-intelligence" => typeof(CodexIntelligencePage),
            "session-plan" => typeof(CodexIntelligencePage),
            "codex-cli" => typeof(CodexCliHarnessPage),
            "codex" => typeof(CodexPage),
            "codex-data" => typeof(CodexDataExplorerPage),
            "forecasts" => typeof(ForecastsPage),
            "model-lab" => typeof(ForecastsPage),
            "coverage" => typeof(CodexRolloutCoveragePage),
            "sources" => typeof(CodexSourcesPage),
            "analytics" => typeof(AnalyticsPage),
            "diagnostics" => typeof(DiagnosticsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(CodexIntelligencePage),
        };

        Navigate(pageType, page == "model-lab" ? "model-lab" : page == "session-plan" ? "planning" : null);
    }

    private sealed record WindowSizeState(int Width, int Height, bool Maximized);
}
