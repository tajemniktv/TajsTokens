// Taj's Tokens | RolloutAccountAssociationTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class RolloutAccountAssociationTests
{
    private static CodexForecastDataset WithHistory()
    {
        CodexForecastDataset data = ComposedQuotaEvaluatorTests.TimelyData();
        QuotaSnapshot[] historical = data.Quota.Select((x, i) => x with
        {
            AccountKey = null,
            Source = "codex-rollout:codex",
            SourceIdentity = "file-generation",
            SessionId = "history",
            ObservationId = $"row-{i}",
            CapturedAtUtc = x.CapturedAtUtc.AddDays(-7),
            ResetsAtUtc = x.ResetsAtUtc?.AddDays(-7),
        }).ToArray();
        return data with
        {
            Quota = historical.Concat(data.Quota).ToArray(),
            Tokens = data.Tokens.Select(x => x with { SessionId = "history", ObservedAtUtc = x.ObservedAtUtc.AddDays(-7) })
                .Concat(data.Tokens).ToArray(),
        };
    }

    [Fact]
    public void AssociationIsBoundedRevocableAndDoesNotInventNativeIdentity()
    {
        CodexForecastDataset data = WithHistory();
        IReadOnlyList<RolloutAccountAssociation> associations = RolloutAccountAssociationPolicy.Create(data, "account", data.CapturedAtUtc);
        RolloutAccountAssociation association = Assert.Single(associations);
        QuotaSnapshot row = data.Quota[1];
        Assert.Equal(association, RolloutAccountAssociationPolicy.Resolve(row, associations, data.CapturedAtUtc));
        Assert.Null(row.AccountKey);
        Assert.Null(RolloutAccountAssociationPolicy.Resolve(row, associations, data.CapturedAtUtc.AddSeconds(-1)));
        Assert.Null(RolloutAccountAssociationPolicy.Resolve(row with { SourceIdentity = "replacement" }, associations, data.CapturedAtUtc));
        Assert.Null(RolloutAccountAssociationPolicy.Resolve(row with { SessionId = "other" }, associations, data.CapturedAtUtc));
        Assert.Null(
            RolloutAccountAssociationPolicy.Resolve(
                row with { CapturedAtUtc = association.ThroughUtc.AddSeconds(1) },
                associations,
                data.CapturedAtUtc));
        Assert.Null(
            RolloutAccountAssociationPolicy.Resolve(
                row,
                [association, association with { Id = "conflict", AccountKey = "other" }],
                data.CapturedAtUtc));
        Assert.Throws<ArgumentException>(() => RolloutAccountAssociationPolicy.Create(data, "invented", data.CapturedAtUtc));
        IReadOnlyList<QuotaCostObservation>
            associated = QuotaCostObservationBuilder.Build(data with { AccountAssociations = associations });
        QuotaCostObservation[] historical = associated.Where(x => x.Attribution == QuotaAccountAttribution.UserAsserted).ToArray();
        Assert.NotEmpty(historical);
        Assert.All(
            historical,
            x =>
            {
                Assert.Null(x.Cohort.AccountKey);
                Assert.Equal("account", x.EffectiveAccountKey);
                Assert.Equal(association.Id, x.AccountAssociationId);
                Assert.Equal(data.CapturedAtUtc, x.EvidenceAvailableAtUtc);
                Assert.Equal(data.CapturedAtUtc, x.TokenCostEvidenceAvailableAtUtc);
            });
        Assert.DoesNotContain(QuotaCostObservationBuilder.Build(data), x => x.Attribution == QuotaAccountAttribution.UserAsserted);
    }

    [Fact]
    public void AssertedTrainingIsDeduplicatedCompatibleAndNeverSuppliesValidationTargets()
    {
        CodexForecastDataset data = WithHistory();
        data = data with { AccountAssociations = RolloutAccountAssociationPolicy.Create(data, "account", data.CapturedAtUtc) };
        IReadOnlyList<QuotaCostObservation> rows = QuotaCostObservationBuilder.Build(data);
        QuotaHistoryCohort cohort = rows.First(x => x.Cohort.AccountKey == "account").Cohort;
        QuotaCostObservation[] native = QuotaCostTrainingPolicy.Select(rows, cohort, 0.5, false);
        QuotaCostObservation[] combined = QuotaCostTrainingPolicy.Select(
            rows.Concat(rows.Where(x => x.Attribution == QuotaAccountAttribution.UserAsserted)).ToArray(),
            cohort,
            0.5,
            true);
        Assert.Equal(20, native.Length);
        Assert.True(combined.Length > native.Length);
        QuotaCostObservation[] asserted = combined.Where(x => x.Attribution == QuotaAccountAttribution.UserAsserted).ToArray();
        Assert.Equal(asserted.Length, asserted.Select(x => (x.StartUtc, x.EndUtc)).Distinct().Count());
        Assert.All(asserted, x => Assert.True(x.EndUtc <= native[0].StartUtc));
        QuotaCostObservation[] incompatible = rows
            .Select(x => x.Attribution == QuotaAccountAttribution.UserAsserted ? x with { EffectiveAccountKey = "other" } : x).ToArray();
        Assert.Equal(native, QuotaCostTrainingPolicy.Select(incompatible, cohort, 0.5, true));
        ComposedQuotaEvaluation evaluation = ComposedQuotaEvaluator.Evaluate(data);
        ComposedQuotaScore candidate =
            evaluation.Scores.Single(x => x.Cohort == cohort && x.HorizonHours == 0.5 && x.CostModel == "asserted-total");
        Assert.Equal(asserted.Length, candidate.AssertedTrainingIntervals);
        Assert.NotEmpty(candidate.Trials);
        Assert.All(candidate.Trials, x => Assert.True(x.OriginUtc >= native[^1].EndUtc));
        // An assertion made today cannot qualify any older prediction as prospective evidence.
        Assert.Empty(
            ComposedQuotaEvaluator.Evaluate(data, availability: ForecastReplayAvailability.CollectedByOrigin).Scores
                .Single(x => x.Cohort == cohort && x.HorizonHours == 0.5 && x.CostModel == "asserted-total").Trials);
        Assert.DoesNotContain(
            ComposedQuotaEvaluator.Evaluate(data with { AccountAssociations = [] }).Scores,
            x => x.CostModel.StartsWith("asserted-", StringComparison.Ordinal));
    }
}