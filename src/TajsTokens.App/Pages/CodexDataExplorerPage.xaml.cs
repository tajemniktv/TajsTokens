// Taj's Tokens | CodexDataExplorerPage.xaml.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

#endregion

namespace TajsTokens.App.Pages;

public sealed partial class CodexDataExplorerPage : Page
{
    private bool _loaded;
    private string? _threadId;

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