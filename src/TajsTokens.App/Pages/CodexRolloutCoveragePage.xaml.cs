// Taj's Tokens | CodexRolloutCoveragePage.xaml.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.App.Pages;

public sealed partial class CodexRolloutCoveragePage : Page
{
    private CancellationTokenSource? _inspection;
    private CodexRolloutInspectionReport? _report;

    public CodexRolloutCoveragePage()
    {
        InitializeComponent();
        Unloaded += (_, _) => _inspection?.Cancel();
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(CodexPage));
    }

    private async void OnInspectClicked(object sender, RoutedEventArgs e)
    {
        await InspectAsync(0);
    }

    private async void OnPreviousClicked(object sender, RoutedEventArgs e)
    {
        await InspectAsync(Math.Max(0, (_report?.Offset ?? 0) - 8));
    }

    private async void OnNextClicked(object sender, RoutedEventArgs e)
    {
        await InspectAsync((_report?.Offset ?? 0) + (_report?.Comparisons.Count ?? 0));
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        _inspection?.Cancel();
    }

    private async Task InspectAsync(int offset)
    {
        if (_inspection is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _inspection = cancellation;
        _report = null;
        ResultsList.ItemsSource = null;
        InspectButton.IsEnabled = PreviousButton.IsEnabled = NextButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusText.Text = "Comparing a bounded sample from the selected collector source…";
        try
        {
            ICodexRolloutInspection service = ((App)Application.Current).Services.CodexRolloutInspection;
            CodexRolloutInspectionReport report = await Task.Run(
                () => service.InspectAlternateRolloutsAsync(offset, cancellation.Token),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _report = report;
            ResultsList.ItemsSource = report.Comparisons.Select(item => new ComparisonRow(
                item.Kind switch
                {
                    CodexRolloutComparisonKind.IdenticalBytes => "Identical captured bytes",
                    CodexRolloutComparisonKind.IdenticalOwnedRecords => "Identical owned records; whole files differ",
                    CodexRolloutComparisonKind.PrefixOverlap => "Exact owned-record prefix overlap",
                    CodexRolloutComparisonKind.DifferentRecords => "Different owned record streams",
                    _ => "Unresolved",
                },
                $"Unindexed: {item.AlternatePath}\nIndexed counterpart: {item.IndexedPath ?? "not established"}",
                $"Ownership: {(item.OwnershipEstablished ? "corroborated" : "unresolved")} · Non-owning prefix observed: {(item.HasNonOwningPrefix ? "yes" : "not established")} · Common owned records: {item.CommonOwnedRecords?.ToString() ?? "unknown"}",
                item.Detail)).ToArray();
            StatusText.Text =
                $"Inspected {report.Comparisons.Count}; unindexed paths: {report.UnindexedPaths?.ToString() ?? "unknown"} (offset {report.Offset}) at {report.ObservedAtUtc.ToLocalTime():g}.\n" +
                $"Selected state catalog: {report.StateDatabasePath ?? "unavailable"}\n{report.Diagnostic}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text =
                "Inspection cancelled or reached its 30-second limit. No partial comparison is presented as complete. Choose Inspect to retry.";
        }
        catch (Exception)
        {
            StatusText.Text =
                "Inspection unavailable. The native source may have changed or become inaccessible; choose Inspect to retry. No files were changed.";
        }
        finally
        {
            _inspection = null;
            InspectButton.IsEnabled = true;
            PreviousButton.IsEnabled = _report?.Offset > 0;
            NextButton.IsEnabled = _report?.HasMore == true;
            CancelButton.IsEnabled = false;
        }
    }

    private sealed record ComparisonRow(string Heading, string Paths, string Evidence, string Detail);
}