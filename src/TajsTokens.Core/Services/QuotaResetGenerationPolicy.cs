using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Derived reset identity only; never rewrites provider timestamps or exact anchors.</summary>
public static class QuotaResetGenerationPolicy
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    public static bool SameTimestamp(DateTimeOffset? a, DateTimeOffset? b) =>
        a is { } left && b is { } right ? (left - right).Duration() <= Tolerance : a == b;

    public static bool SameGeneration(QuotaSnapshot a, QuotaSnapshot b) =>
        QuotaHistoryPolicy.Cohort(a) == QuotaHistoryPolicy.Cohort(b) && SameTimestamp(a.ResetsAtUtc, b.ResetsAtUtc);

    public static bool FitsRange(DateTimeOffset reset, DateTimeOffset minimum, DateTimeOffset maximum) =>
        SameTimestamp(reset, minimum) && SameTimestamp(reset, maximum);

    // Callers supply one compatible cohort. Bound the whole range, not adjacent differences:
    // T, T+1s, T+2s must not become a single generation by transitive chaining.
    public static IReadOnlyList<IReadOnlyList<T>> Group<T>(IEnumerable<T> rows, Func<T, DateTimeOffset> reset)
    {
        var groups = new List<IReadOnlyList<T>>();
        List<T>? current = null;
        foreach (var row in rows.OrderBy(reset))
        {
            if (current is null || !SameTimestamp(reset(row), reset(current[0])))
            {
                current = [];
                groups.Add(current);
            }
            current.Add(row);
        }
        return groups;
    }
}
