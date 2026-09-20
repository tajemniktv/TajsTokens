// Taj's Tokens | NativeFirstCodexAccountingProvider.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Infrastructure.Providers;

/// <summary>
///     Native-first Codex accounting policy. Tokscale is never invoked on the ordinary path; users can
///     explicitly enable it as a reconciliation oracle and/or fallback while the native cutover is
///     validated. This keeps provider breadth without making `npx tokScale@latest` a background tax.
/// </summary>
public sealed class NativeFirstCodexAccountingProvider(
    ICodexTokenAccountingProvider nativeProvider,
    ITokscaleProvider tokscaleProvider,
    Func<bool> reconciliationEnabled,
    Func<bool> fallbackEnabled) : ICodexTokenAccountingProvider
{
    private readonly ICodexTokenAccountingProvider _tokscaleAccounting = tokscaleProvider;

    private static TokenBreakdown Zero { get; } = new(0, 0, 0, 0, 0, 0);

    public async Task<CodexTokenAccountingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        CodexTokenAccountingSnapshot native;
        try
        {
            native = await nativeProvider.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception nativeException) when (nativeException is not OperationCanceledException && fallbackEnabled())
        {
            CodexTokenAccountingSnapshot fallback = await _tokscaleAccounting.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return fallback with
            {
                Source = "Tokscale fallback",
                IsFallback = true,
                Diagnostic = $"Native accounting unavailable: {Summarize(nativeException.Message)}",
            };
        }

        if (!reconciliationEnabled())
        {
            return native;
        }

        try
        {
            CodexTokenAccountingSnapshot reference = await _tokscaleAccounting.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            CodexAccountingReconciliation reconciliation = Reconcile(native, reference);
            return native with { Reconciliation = reconciliation, Diagnostic = FormatReconciliation(reconciliation) };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Reconciliation is diagnostic only. A missing/broken optional oracle must never make a
            // healthy native accounting generation stale or unavailable.
            return native with { Diagnostic = $"Tokscale reconciliation unavailable: {Summarize(exception.Message)}" };
        }
    }

    internal static CodexAccountingReconciliation Reconcile(
        CodexTokenAccountingSnapshot native,
        CodexTokenAccountingSnapshot reference)
    {
        TokenBreakdown nativeTotals = Sum(native.Usage.Select(item => item.Breakdown));
        TokenBreakdown referenceTotals = Sum(reference.Usage.Select(item => item.Breakdown));
        long totalDifference = nativeTotals.Total - referenceTotals.Total;
        double totalDifferencePercent = referenceTotals.Total == 0
            ? nativeTotals.Total == 0 ? 0 : 100
            : 100d * totalDifference / referenceTotals.Total;

        Dictionary<string, TokenBreakdown> nativeModels = native.Usage
            .GroupBy(item => NormalizeModel(item.Model), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => Sum(group.Select(item => item.Breakdown)), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, TokenBreakdown> referenceModels = reference.Usage
            .GroupBy(item => NormalizeModel(item.Model), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => Sum(group.Select(item => item.Breakdown)), StringComparer.OrdinalIgnoreCase);
        string[] modelKeys = nativeModels.Keys.Union(referenceModels.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        int modelDifferences = modelKeys.Count(key =>
            !BreakdownsEqual(nativeModels.GetValueOrDefault(key), referenceModels.GetValueOrDefault(key)));

        // Tokscale hourly JSON can expose only date/hour labels, while the native projection always
        // has an offset-aware timestamp. Comparing one side in UTC and the other by label makes every
        // bucket look different. Use UTC only when both generations can do so; otherwise compare the
        // two providers on their common display-label representation.
        bool useUtcHourKeys = native.Hourly.All(item => item.StartUtc is not null) &&
                              reference.Hourly.All(item => item.StartUtc is not null);
        Dictionary<string, TokenBreakdown> nativeHours = BuildHourMap(native.Hourly, useUtcHourKeys);
        Dictionary<string, TokenBreakdown> referenceHours = BuildHourMap(reference.Hourly, useUtcHourKeys);
        string[] hourKeys = nativeHours.Keys.Union(referenceHours.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        int hourDifferences = hourKeys.Count(key =>
            !BreakdownsEqual(nativeHours.GetValueOrDefault(key), referenceHours.GetValueOrDefault(key)));

        return new CodexAccountingReconciliation(
            nativeTotals,
            referenceTotals,
            totalDifference,
            totalDifferencePercent,
            modelKeys.Length,
            modelDifferences,
            hourKeys.Length,
            hourDifferences);
    }

    internal static string FormatReconciliation(CodexAccountingReconciliation value)
    {
        if (value.Exact)
        {
            return
                $"Tokscale reconciliation exact across {value.ModelBucketsCompared} model bucket(s) and {value.HourBucketsCompared} hourly bucket(s).";
        }

        return $"Tokscale reconciliation: total Δ {value.TotalDifference:+#;-#;0} ({value.TotalDifferencePercent:+0.###;-0.###;0}%), " +
               $"{value.ModelBucketsDifferent}/{value.ModelBucketsCompared} model bucket(s) differ, " +
               $"{value.HourBucketsDifferent}/{value.HourBucketsCompared} hourly bucket(s) differ.";
    }

    private static Dictionary<string, TokenBreakdown> BuildHourMap(
        IReadOnlyList<TokenTimeBucket> buckets,
        bool useUtc)
    {
        return buckets
            .GroupBy(bucket => HourKey(bucket, useUtc), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => Sum(group.Select(item => item.Breakdown)), StringComparer.OrdinalIgnoreCase);
    }

    private static string HourKey(TokenTimeBucket bucket, bool useUtc)
    {
        if (useUtc && bucket.StartUtc is DateTimeOffset start)
        {
            return start.ToUniversalTime().ToString("yyyy-MM-ddTHH");
        }

        return bucket.Label.Trim();
    }

    private static string NormalizeModel(string? model)
    {
        return string.IsNullOrWhiteSpace(model) ? "(unknown)" : model.Trim();
    }

    private static bool BreakdownsEqual(TokenBreakdown? left, TokenBreakdown? right)
    {
        left ??= Zero;
        right ??= Zero;
        return left.UncachedInput == right.UncachedInput &&
               left.CacheRead == right.CacheRead &&
               left.CacheWrite == right.CacheWrite &&
               left.NonReasoningOutput == right.NonReasoningOutput &&
               left.ReasoningOutput == right.ReasoningOutput &&
               left.Total == right.Total;
    }

    private static TokenBreakdown Sum(IEnumerable<TokenBreakdown> values)
    {
        long uncached = 0;
        long cacheRead = 0;
        long cacheWrite = 0;
        long output = 0;
        long reasoning = 0;
        long reported = 0;
        bool hasReported = false;

        checked
        {
            foreach (TokenBreakdown value in values)
            {
                uncached += value.UncachedInput;
                cacheRead += value.CacheRead;
                cacheWrite += value.CacheWrite;
                output += value.NonReasoningOutput;
                reasoning += value.ReasoningOutput;
                if (value.ReportedTotal is long total)
                {
                    reported += total;
                    hasReported = true;
                }
            }
        }

        return new TokenBreakdown(
            uncached,
            cacheRead,
            cacheWrite,
            output,
            reasoning,
            hasReported ? reported : null);
    }

    private static string Summarize(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 240 ? value : value[..240] + "…";
    }
}