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
using TajsTokens.App.Pages;

#endregion

namespace TajsTokens.App;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _saveWindowTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly string _windowStatePath = Path.Combine(((App)Application.Current).Services.DataFolder, "window-state.json");
    private bool _synchronizing;
    private WindowSizeState _windowSize = new(1200, 850, false);

    public MainWindow()
    {
        InitializeComponent();
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
        Navigate(typeof(CodexIntelligencePage));
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
        if (ContentFrame is null) return;
        if (ContentFrame.CurrentSourcePageType == pageType && Equals(ContentFrame.Tag, parameter)) return;
        ContentFrame.Tag = parameter;
        ContentFrame.Navigate(pageType, parameter);
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
            AppNavigation.IsBackEnabled = ContentFrame.CanGoBack;
            string tag = e.SourcePageType == typeof(CodexIntelligencePage) ? "codex-intelligence" :
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
        if (_synchronizing) return;
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

        Navigate(pageType, page == "model-lab" ? "model-lab" : null);
    }

    private sealed record WindowSizeState(int Width, int Height, bool Maximized);
}