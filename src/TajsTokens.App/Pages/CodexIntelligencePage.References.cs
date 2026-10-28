using Microsoft.UI.Xaml;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using Windows.Storage.Pickers;

namespace TajsTokens.App.Pages;

public sealed partial class CodexIntelligencePage
{
    private void RenderReferenceDetails()
    {
        var reference = _snapshot?.Reference;
        SaveTtButton.IsEnabled = reference?.Sources.Selection is not null && _snapshot?.JointWorkload is not null;
        if (reference is null) { ReferenceDetails.Text = "Reference data unavailable. Native tokens remain usable."; return; }
        string Source(string label, ApiPriceCatalogSource? source) => source is null ? $"{label}: disabled" :
            $"{label}: {source.LastGood?.SourceName ?? "unavailable"} · last import {source.LastState?.ToString() ?? "not imported"}" +
            $"\nSnapshot: {source.LastGood?.SnapshotId ?? "unavailable"}; source verified {source.LastGood?.RetrievedAtUtc?.ToString("O") ?? "unknown"}; " +
            $"first imported {source.FirstImportedAtUtc?.ToString("O") ?? "unknown"}; stored invalid={source.InvalidStoredSnapshot}";
        string Price(string label, ApiPriceValuation? price) => price is null ? $"{label}: unavailable" :
            $"{label}: {price.State}; whole USD {price.FullAmountUsd?.ToString("N6") ?? "unavailable"}; covered USD {price.CoveredAmountUsd?.ToString("N6") ?? "unavailable"}" +
            $"\n{price.PricedTokens:N0} priced / {price.UnpricedTokens:N0} unpriced tokens; stale={price.UsesStaleSource}; unknown source clock={price.HasUnknownSourceTime}; future source clock={price.HasFutureSourceTime}." +
            string.Concat(price.Contributions.Select(x => $"\n{x.Origin} · {x.SourceName}: ${x.AmountUsd:N6}; {x.Tokens:N0} tokens · {x.SnapshotId}"));
        ReferenceDetails.Text = reference.TtSummary + "\n" + reference.BasisLabel +
            $"\nBasis: {reference.Sources.Selection?.Basis.BasisId ?? "unavailable"}\n" +
            (reference.Sources.Selection?.Basis.Convention ?? "") + "\n" + reference.Tt?.Explanation +
            "\n\n" + Source("Official prices", reference.Sources.Prices) + "\n" + Source("Overrides", reference.Sources.Overrides) +
            "\n" + Source("Fallback", reference.Sources.Fallback) + "\n\n" + Price("Current snapshot reference", reference.CurrentPrice) +
            "\n\n" + Price("Date-effective reference", reference.DateEffectivePrice) +
            "\nUnknown effective dates stay unpriced in date-effective mode. Both modes use the declared reference context, not inferred billing conditions." +
            "\nRefresh: explicit imports; stale after 30 days. No network polling. Prices are not invoices, credits or subscription quota." +
            "\nVersions: public tt-joint-basis/v1 + tt-joint-scoring/v1; research tt-category-basis/v1; evaluator tt-lab/v4-blocks." +
            (reference.Sources.Issue is { } issue ? "\nReference issue: " + issue : "");
    }

    private async Task<string?> PickReferenceFileAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private async Task RunReferenceAction(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is not OutOfMemoryException)
        { if (_loaded) ReferenceActionStatus.Text = $"Reference action failed ({e.GetType().Name}). Previous snapshots are retained."; }
    }

    private async void OnImportPrice(object sender, RoutedEventArgs e) => await RunReferenceAction(async () =>
    {
        int role = PriceImportRole.SelectedIndex;
        string? file = await PickReferenceFileAsync();
        if (!_loaded || file is null) return;
        InvalidatePlan();
        var attempt = await App.Services.WorkloadReferences.ImportAsync(file, role switch
        { 1 => ApiPriceOrigin.LocalOverride, 2 => ApiPriceOrigin.CommunityFallback, _ => ApiPriceOrigin.OfficialCurated }, default);
        await LoadAsync();
        if (_loaded) ReferenceActionStatus.Text = attempt.State == ApiPriceImportState.Accepted
            ? "Price snapshot imported. TT basis unchanged." : "Import unavailable or invalid. Last good snapshot retained.";
    });

    private async void OnSelectTtBasis(object sender, RoutedEventArgs e) => await RunReferenceAction(async () =>
    {
        string? file = await PickReferenceFileAsync();
        if (!_loaded || file is null) return;
        InvalidatePlan();
        await App.Services.WorkloadReferences.SelectBasisAsync(file, default);
        await LoadAsync();
        if (_loaded) ReferenceActionStatus.Text = "TT basis selected for future views. Saved calculations are unchanged.";
    });

    private async void OnDisableOptionalPrices(object sender, RoutedEventArgs e) => await RunReferenceAction(async () =>
    {
        InvalidatePlan();
        await App.Services.WorkloadReferences.SetOptionalSourcesAsync(false, false, false, default); await LoadAsync();
        if (_loaded) ReferenceActionStatus.Text = "Overrides and fallback disabled. TT basis unchanged.";
    });

    private async void OnLegacyPrices(object sender, RoutedEventArgs e) => await RunReferenceAction(async () =>
    {
        InvalidatePlan();
        await App.Services.WorkloadReferences.SetOptionalSourcesAsync(_snapshot?.Reference?.Sources.Selection?.OverrideSourceKey is not null,
            true, true, default); await LoadAsync();
        if (_loaded) ReferenceActionStatus.Text = "Legacy fallback enabled and labelled. TT basis unchanged.";
    });

    private async void OnSaveTt(object sender, RoutedEventArgs e) => await RunReferenceAction(async () =>
    {
        if (_snapshot is not { Reference.Sources.Selection: { } selected, JointWorkload: { } workload, DatasetCapturedAtUtc: { } captured } snapshot) return;
        SaveTtButton.IsEnabled = false;
        var original = new TtJointScoreSnapshot(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, captured, "codex", "default",
            snapshot.Selection.FromUtc, snapshot.Selection.ToUtc, selected.Basis, workload, selected.Basis.ScoreExact(workload))
            { Assessment = snapshot.Reference.Tt, Selection = snapshot.Selection };
        try
        {
            await App.Services.Repository.SaveTtJointScoreAsync(original, default);
            if (_loaded) ReferenceActionStatus.Text = "Original TT calculation saved with its frozen basis and captured workload.";
        }
        finally { if (_loaded) SaveTtButton.IsEnabled = true; }
    });
}
