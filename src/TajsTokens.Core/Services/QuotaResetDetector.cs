// Taj's Tokens | QuotaResetDetector.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

/// <summary>
///     Detects observable reset/re-anchor transitions from provider quota history. The provider's current
///     window/reset identity is authoritative; local time arithmetic is used only to classify evidence.
/// </summary>
public sealed class QuotaResetDetector
{
    private static readonly TimeSpan s_expectedBoundaryTolerance = TimeSpan.FromMinutes(30);

    public IReadOnlyList<QuotaResetEvent> Detect(IReadOnlyList<QuotaSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var events = new List<QuotaResetEvent>();
        foreach (IGrouping<QuotaHistoryCohort, QuotaSnapshot> group in snapshots
                     .Where(snapshot => snapshot.Kind is QuotaWindowKind.FiveHour or QuotaWindowKind.Weekly)
                     .GroupBy(QuotaHistoryPolicy.Cohort))
        {
            QuotaSnapshot[] ordered = group.OrderBy(snapshot => snapshot.CapturedAtUtc).ToArray();
            DateTimeOffset? minimumReset = ordered[0].ResetsAtUtc, maximumReset = minimumReset;
            for (int index = 1; index < ordered.Length; index++)
            {
                QuotaSnapshot previous = ordered[index - 1];
                QuotaSnapshot current = ordered[index];
                if (previous.UsedPercent is null || current.UsedPercent is null)
                {
                    minimumReset = maximumReset = current.ResetsAtUtc;
                    continue;
                }

                double drop = previous.UsedPercent.Value - current.UsedPercent.Value;
                bool resetIdentityChanged = !QuotaResetGenerationPolicy.SameGeneration(previous, current) ||
                                            current.ResetsAtUtc is { } reset && minimumReset is { } minimum &&
                                            maximumReset is { } maximum &&
                                            !QuotaResetGenerationPolicy.FitsRange(reset, minimum, maximum);
                if (resetIdentityChanged)
                {
                    minimumReset = maximumReset = current.ResetsAtUtc;
                }
                else if (current.ResetsAtUtc is { } observedReset)
                {
                    minimumReset = minimumReset is { } min && min < observedReset ? min : observedReset;
                    maximumReset = maximumReset is { } max && max > observedReset ? max : observedReset;
                }
                DateTimeOffset? previousBoundary = previous.ResetsAtUtc;
                bool nearExpectedBoundary = previousBoundary is DateTimeOffset expected &&
                                            current.CapturedAtUtc >= expected - s_expectedBoundaryTolerance &&
                                            current.CapturedAtUtc <= expected + s_expectedBoundaryTolerance;

                QuotaResetClassification? classification = null;
                double confidence = 0;
                string explanation = string.Empty;
                DateTimeOffset effectiveAt = current.CapturedAtUtc;

                if (resetIdentityChanged)
                {
                    if (drop >= 1 && nearExpectedBoundary)
                    {
                        classification = QuotaResetClassification.ExpectedReset;
                        confidence = Math.Clamp(0.88 + Math.Min(drop, 50) / 500d, 0, 0.98);
                        effectiveAt = previousBoundary ?? current.CapturedAtUtc;
                        explanation =
                            "Quota decreased while the provider advanced the window identity near the previous authoritative reset boundary.";
                    }
                    else if (drop >= 50)
                    {
                        classification = QuotaResetClassification.FullReset;
                        confidence = nearExpectedBoundary ? 0.9 : 0.78;
                        explanation =
                            "Quota dropped substantially while the provider changed the window identity; classified as a full-reset observation rather than a token-derived inference.";
                    }
                    else if (drop >= 5)
                    {
                        classification = QuotaResetClassification.UnusualReset;
                        confidence = nearExpectedBoundary ? 0.8 : 0.68;
                        explanation =
                            "Quota decreased materially while the provider changed the window identity outside the clean expected-reset pattern.";
                    }
                    else if (drop >= 1)
                    {
                        classification = QuotaResetClassification.UnusualReset;
                        confidence = 0.55;
                        explanation =
                            "Quota decreased slightly while the provider changed the window identity outside the expected boundary. The evidence is ambiguous, so it is not treated as a clean rolling-window re-anchor.";
                    }
                    else if (current.ResetsAtUtc is DateTimeOffset currentReset &&
                             previous.ResetsAtUtc is DateTimeOffset previousReset &&
                             currentReset > previousReset)
                    {
                        classification = QuotaResetClassification.ReanchoredWindow;
                        confidence = previousReset <= current.CapturedAtUtc ? 0.92 : 0.72;
                        explanation =
                            "The provider advanced the reset/window identity without a material visible meter drop; this is recorded as a rolling-window re-anchor, not a phantom missed reset.";
                    }
                }
                else if (drop >= 50)
                {
                    classification = QuotaResetClassification.FullReset;
                    confidence = 0.62;
                    explanation =
                        "Quota dropped by at least 50 percentage points without an observed window-identity change. This is unusual evidence and remains explicitly lower confidence.";
                }
                else if (drop >= 5)
                {
                    classification = QuotaResetClassification.UnusualReset;
                    confidence = 0.5;
                    explanation =
                        "Quota decreased materially without an observed reset-identity transition. This may reflect delayed provider propagation or correction and is not treated as provider-confirmed scheduling.";
                }

                if (classification is null)
                {
                    continue;
                }

                string source = string.Equals(previous.Source, current.Source, StringComparison.Ordinal)
                    ? current.Source
                    : $"{previous.Source} → {current.Source}";
                events.Add(
                    new QuotaResetEvent(
                        BuildEventId(previous, current),
                        current.Kind,
                        current.Provider,
                        current.Profile,
                        current.CapturedAtUtc,
                        effectiveAt,
                        previous.UsedPercent,
                        current.UsedPercent,
                        previous.ResetsAtUtc,
                        current.ResetsAtUtc,
                        classification.Value,
                        confidence,
                        source,
                        explanation,
                        current.AccountKey));
            }
        }

        return events;
    }

    private static string BuildEventId(QuotaSnapshot previous, QuotaSnapshot current)
    {
        // Event identity belongs to the observation pair, not its current interpretation. A provider
        // correction or newly-arrived reset metadata should update this event instead of creating a
        // second historical reset for the same adjacent observations.
        // Structured encoding preserves nulls and delimiter-bearing identifiers without collisions.
        string material = JsonSerializer.Serialize(
            new
            {
                Cohort = QuotaHistoryPolicy.Cohort(current),
                Previous = previous.CapturedAtUtc.ToUniversalTime().ToString("O"),
                Current = current.CapturedAtUtc.ToUniversalTime().ToString("O"),
            });
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return $"quota-reset-v2-{hash}";
    }
}