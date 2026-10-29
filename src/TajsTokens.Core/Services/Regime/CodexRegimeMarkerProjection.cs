using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public sealed record CodexRegimeMarker(string State, string Summary, string Detail);

/// <summary>Selected-history presentation over the existing frozen-reference analysis; no fitting or live alarm.</summary>
public static class CodexRegimeMarkerProjection
{
    public const string Version = "regime-marker/v1";
    public static CodexRegimeMarker Build(CodexSelection? selection, QuotaCostReport? accounting, CodexRegimeReport? regime,
        string? manifestId = null)
    {
        if (selection?.HasWorkFilter == true)
            return new("filtered", "History relationship · clear work filters", "Model/project/chat work cannot be assigned account quota. Clear those filters before analysis.");
        if (selection is null || accounting is null || regime is null)
            return new("not-assessed", "History relationship · not assessed", "Run Accounting and regime research for the selected history. This is separate from live allowance forecasts.");
        var supported = accounting.Scores.Where(s => s.Candidate is "categories" or "total" && s.TrainingSamples >= 20 && s.Trials.Count >= 16 &&
            QuotaSnapshot.ClassifyAuthority(s.Cohort.Source) == QuotaObservationAuthority.ProviderAuthoritative &&
            !string.IsNullOrWhiteSpace(s.Cohort.AccountKey) && (selection.AccountKey is null || selection.AccountKey == s.Cohort.AccountKey) &&
            s.Trials.All(t => t.StartUtc >= selection.FromUtc && t.EndUtc <= selection.ToUtc))
            .Select(s => (s.Cohort, s.HorizonHours)).Distinct().ToHashSet();
        int candidates = regime.Candidates.Count(c => supported.Contains((c.Cohort, c.HorizonHours)) &&
            c.BoundaryLowerUtc >= selection.FromUtc && c.BoundaryUpperUtc <= selection.ToUtc);
        string detail = $"Selected UTC history {selection.FromUtc:yyyy-MM-dd}–{selection.ToUtc.AddTicks(-1):yyyy-MM-dd}; " +
            $"{supported.Count} account-qualified cohort/horizon groups. Frozen cost reference: {accounting.Version}; analysis: {manifestId ?? "unavailable"}. " +
            (accounting.DatasetCapturedAtUtc is { } at ? $"Evidence read {at:O}. " : "Evidence-read time unavailable. ") +
            "Retrospective evidence, not a live or causal provider-policy alert. Plan/tier, mixture, coverage, attribution, precision and lag remain competing explanations. " +
            "No candidate is not proof of stability. The selected TT ruler remains separate.";
        return supported.Count == 0
            ? new("insufficient", "History relationship · insufficient evidence", detail)
            : candidates == 0 ? new("no-candidate", "History relationship · no supported candidate", detail)
            : new("candidate", "History relationship · unexplained change", $"{candidates} retrospective candidate(s). " + detail);
    }
}
