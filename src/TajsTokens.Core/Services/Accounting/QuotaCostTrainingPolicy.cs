// Taj's Tokens | QuotaCostTrainingPolicy.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

public static class QuotaCostTrainingPolicy
{
    public static QuotaCostObservation[] Select(
        IReadOnlyList<QuotaCostObservation> rows,
        QuotaHistoryCohort cohort,
        double horizon,
        bool useAssertions)
    {
        QuotaCostObservation[] native = rows.Where(x => x.Cohort == cohort && x.HorizonHours == horizon).OrderBy(x => x.StartUtc).Take(20)
            .ToArray();
        if (!useAssertions || native.Length < 20 || cohort.AccountKey is null || cohort.PlanType is null ||
            cohort.LimitId is null) return native;
        var selected = new List<QuotaCostObservation>();
        DateTimeOffset? end = null;
        foreach (QuotaCostObservation row in rows.Where(x => x.Attribution == QuotaAccountAttribution.UserAsserted &&
                                                             x.EffectiveAccountKey == cohort.AccountKey && x.HorizonHours == horizon &&
                                                             x.Cohort.Provider == cohort.Provider && x.Cohort.Profile == cohort.Profile &&
                                                             x.Cohort.Kind == cohort.Kind &&
                                                             x.Cohort.WindowMinutes == cohort.WindowMinutes &&
                                                             x.Cohort.PlanType == cohort.PlanType && x.Cohort.LimitId == cohort.LimitId &&
                                                             x.EndUtc <= native[0].StartUtc)
                     .OrderBy(x => x.StartUtc).ThenBy(x => x.EndUtc).ThenBy(x => x.Cohort.Source, StringComparer.Ordinal)
                     .ThenBy(x => x.Cohort.SessionId, StringComparer.Ordinal))
        {
            if (row.StartUtc < end) continue;
            selected.Add(row);
            end = row.EndUtc;
        }
        // Assertions supplement training only, never validation generations. Preserve raw cohorts.
        return selected.TakeLast(120).Concat(native).ToArray();
    }
}