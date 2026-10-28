// Taj's Tokens | CodexDataExplorerPage.xaml.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

#endregion

namespace TajsTokens.App.Pages;

public sealed partial class CodexDataExplorerPage : Page, IHistoryPresentation
{
    private bool _loaded;
    private string? _threadId;

    public void ReleaseHistory()
    {
        if (ExplorerFrame.Content is IHistoryPresentation history) history.ReleaseHistory();
    }

    public CodexDataExplorerPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _threadId = e.Parameter switch
        {
            CodexDataExplorerRequest request => request.ThreadId,
            string text when text.StartsWith("thread:", StringComparison.OrdinalIgnoreCase) => text[7..],
            _ => null,
        };
        FilterHintText.Text = string.IsNullOrWhiteSpace(_threadId)
            ? "Select a source family, then inspect its bounded observations."
            : $"Thread filter: {_threadId}";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        if (_restoredExplorer is { } restored)
        {
            _restoredExplorer = null;
            // Restore before attaching the child, so its Loaded query uses saved filters.
            ExplorerFrame.Navigated += RestoreExplorerInputs;
            try { ExplorerFrame.Navigate(restored.Type, _threadId); }
            finally { ExplorerFrame.Navigated -= RestoreExplorerInputs; }
            SourcesButton.IsEnabled = restored.Type != typeof(CodexSourcesPage);
            RawButton.IsEnabled = restored.Type != typeof(CodexStateDbExplorerPage);
            return;

            void RestoreExplorerInputs(object sender, NavigationEventArgs args)
            {
                if (restored.Inputs is not null && args.Content is IDashboardPresentation presentation)
                    presentation.RestoreInputs(restored.Inputs);
            }
        }
        ShowSources();
    }

    private void OnSourcesClicked(object sender, RoutedEventArgs e)
    {
        ShowSources();
    }

    private void OnRawClicked(object sender, RoutedEventArgs e)
    {
        ExplorerFrame.Navigate(typeof(CodexStateDbExplorerPage), _threadId);
        SourcesButton.IsEnabled = true;
        RawButton.IsEnabled = false;
    }

    private void ShowSources()
    {
        ExplorerFrame.Navigate(typeof(CodexSourcesPage), _threadId);
        SourcesButton.IsEnabled = false;
        RawButton.IsEnabled = true;
    }
}
