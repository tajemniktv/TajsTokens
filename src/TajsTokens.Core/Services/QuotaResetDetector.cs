using System.Security.Cryptography;
using System.Text;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>
/// Detects observable reset/re-anchor transitions from provider quota history. The provider's current
/// window/reset identity is authoritative; local time arithmetic is used only to classify evidence.
/// </summary>
public sealed class QuotaResetDetector
{
    private static readonly TimeSpan ExpectedBoundaryTolerance = TimeSpan.FromMinutes(30);

    public IReadOnlyList<QuotaResetEvent> Detect(IReadOnlyList<QuotaSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var events = new List<QuotaResetEvent>();
        foreach (var group in snapshots
                     .Where(snapshot => snapshot.Kind is QuotaWindowKind.FiveHour or QuotaWindowKind.Weekly)
                     .GroupBy(snapshot => (snapshot.Provider, snapshot.Profile, snapshot.Kind)))
        {
            var ordered = group.OrderBy(snapshot => snapshot.CapturedAtUtc).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                if (previous.UsedPercent is null || current.UsedPercent is null)
                {
                    continue;
                }

                var drop = previous.UsedPercent.Value - current.UsedPercent.Value;
                var resetIdentityChanged = previous.WindowMinutes != current.WindowMinutes ||
                                           previous.ResetsAtUtc != current.ResetsAtUtc;
                var previousBoundary = previous.ResetsAtUtc;
                var nearExpectedBoundary = previousBoundary is DateTimeOffset expected &&
                                           current.CapturedAtUtc >= expected - ExpectedBoundaryTolerance &&
                                           current.CapturedAtUtc <= expected + ExpectedBoundaryTolerance;

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
                        explanation = "Quota decreased while the provider advanced the window identity near the previous authoritative reset boundary.";
                    }
                    else if (drop >= 50)
                    {
                        classification = QuotaResetClassification.FullReset;
                        confidence = nearExpectedBoundary ? 0.9 : 0.78;
                        explanation = "Quota dropped substantially while the provider changed the window identity; classified as a full-reset observation rather than a token-derived inference.";
                    }
                    else if (drop >= 5)
                    {
                        classification = QuotaResetClassification.UnusualReset;
                        confidence = nearExpectedBoundary ? 0.8 : 0.68;
                        explanation = "Quota decreased materially while the provider changed the window identity outside the clean expected-reset pattern.";
                    }
                    else if (drop >= 1)
                    {
                        classification = QuotaResetClassification.UnusualReset;
                        confidence = 0.55;
                        explanation = "Quota decreased slightly while the provider changed the window identity outside the expected boundary. The evidence is ambiguous, so it is not treated as a clean rolling-window re-anchor.";
                    }
                    else if (current.ResetsAtUtc is DateTimeOffset currentReset &&
                             previous.ResetsAtUtc is DateTimeOffset previousReset &&
                             currentReset > previousReset)
                    {
                        classification = QuotaResetClassification.ReanchoredWindow;
                        confidence = previousReset <= current.CapturedAtUtc ? 0.92 : 0.72;
                        explanation = "The provider advanced the reset/window identity without a material visible meter drop; this is recorded as a rolling-window re-anchor, not a phantom missed reset.";
                    }
                }
                else if (drop >= 50)
                {
                    classification = QuotaResetClassification.FullReset;
                    confidence = 0.62;
                    explanation = "Quota dropped by at least 50 percentage points without an observed window-identity change. This is unusual evidence and remains explicitly lower confidence.";
                }
                else if (drop >= 5)
                {
                    classification = QuotaResetClassification.UnusualReset;
                    confidence = 0.5;
                    explanation = "Quota decreased materially without an observed reset-identity transition. This may reflect delayed provider propagation or correction and is not treated as provider-confirmed scheduling.";
                }

                if (classification is null)
                {
                    continue;
                }

                var source = string.Equals(previous.Source, current.Source, StringComparison.Ordinal)
                    ? current.Source
                    : $"{previous.Source} → {current.Source}";
                events.Add(new QuotaResetEvent(
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
                    explanation));
            }
        }

        return events;
    }

    private static string BuildEventId(QuotaSnapshot previous, QuotaSnapshot current)
    {
        // Event identity belongs to the observation pair, not its current interpretation. A provider
        // correction or newly-arrived reset metadata should update this event instead of creating a
        // second historical reset for the same adjacent observations.
        var material = string.Join('|',
            current.Provider,
            current.Profile,
            current.Kind,
            previous.CapturedAtUtc.ToUniversalTime().ToString("O"),
            current.CapturedAtUtc.ToUniversalTime().ToString("O"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return $"quota-reset-{hash[..24]}";
    }
}
