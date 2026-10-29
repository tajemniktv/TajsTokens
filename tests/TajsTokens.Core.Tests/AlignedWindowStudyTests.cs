using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class AlignedWindowStudyTests
{
    internal static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    internal static QuotaCostObservation[] Rows(bool proportional = false, int count = 100) => Enumerable.Range(0, count).SelectMany(i =>
    {
        var origin = Start.AddHours(i * 6); var end = origin.AddMinutes(30);
        long input = (1 + i % 5) * 100000, output = (1 + i * 7 % 4) * 100000;
        var data = new CodexForecastDataset([], [],
            [new("s", origin.AddMinutes(10), origin.AddMinutes(10), "a", "high", input, 0, 0, 0, 0, input),
             new("s", origin.AddMinutes(20), origin.AddMinutes(20), "b", "high", 0, 0, 0, output, 0, output)], [], end, "Synthetic paired work");
        var features = CodexForecastFeatureBuilder.Build(data, end, .5);
        var cohort = new QuotaHistoryCohort("codex", "default", QuotaWindowKind.FiveHour, "codex-app-server:codex", "private-account", "codex", "pro", null, 300);
        double five = input / 1e6 * 2 + output / 1e6 * 8, week = proportional ? five * 3 : input / 1e6 * 9 + output / 1e6;
        var row = new QuotaCostObservation(cohort, origin, origin.AddHours(5), origin, end, .5, 0, five, five, five,
            "synthetic-point", 0, [input, 0, 0, output, 0], features, null, 0, [])
            { TokenCostEvidenceAvailableAtUtc = end, OriginCollectedAtUtc = origin, OutcomeCollectedAtUtc = end, OriginEvidenceAvailableAtUtc = origin };
        return new[] { row, row with { Cohort = cohort with { Kind = QuotaWindowKind.Weekly, WindowMinutes = 10080 }, ResetUtc = origin.AddDays(7),
            EndUsed = week, LowerDelta = week, UpperDelta = week } };
    }).ToArray();
    private static AlignedWindowComparison Run(QuotaCostObservation[] rows) => QuotaTransferEvaluator.EvaluateAligned(rows,
        Start, Start.AddHours(600), ForecastReplayAvailability.CollectedByOrigin, includeTrials: true);

    [Fact]
    public void AlignedControlsShareTrainingAndTargetsWithoutChangingTheirInput()
    {
        var rows = Rows(true); string before = JsonSerializer.Serialize(rows); var report = Run(rows);
        Assert.Equal(100, report.AlignedIntervals); var group = Assert.Single(report.Groups);
        Assert.Equal(6, group.Models.Count); Assert.Equal(Start.AddHours(480), report.SplitUtc);
        foreach (var model in group.Models)
        {
            Assert.Equal("research-only", model.Status); Assert.Equal(model.TrainingPolicy == "first20" ? 20 : 80, model.TrainingRows);
            Assert.True(model.TrainingThroughUtc <= report.SplitUtc); Assert.True(model.TrainingAvailableUtc <= report.SplitUtc);
            Assert.True(model.FiveToWeeklyScale > 0); Assert.True(model.WeeklyToFiveScale > 0);
            Assert.All(model.Scores, s => Assert.Equal(s.Origins, s.Matched));
            Assert.All(model.Trials.Where(t => t.Period == "final"), t => Assert.True(t.OriginUtc >= report.SplitUtc));
        }
        Assert.Equal(before, JsonSerializer.Serialize(rows));
        Assert.DoesNotContain("private-account", JsonSerializer.Serialize(report));
    }

    [Fact]
    public void MixtureDependentRatiosCanDefeatTheDirectionalScalar()
    {
        var model = Run(Rows()).Groups.Single().Models.Single(m => m.Model == "categories" && m.TrainingPolicy == "development-prefix");
        var final = model.Scores.Single(s => s.Window == "Weekly" && s.Period == "final");
        Assert.True(final.SharedMae > final.SeparateMae); Assert.True(final.SharedBlockLoss > final.SeparateBlockLoss);
    }

    [Fact]
    public void FutureLabelsCannotTuneScaleTrainingOrTheDevelopmentSummary()
    {
        var rows = Rows(); var before = Run(rows);
        var after = Run(rows.Select(r => r.Cohort.Kind == QuotaWindowKind.Weekly && r.StartUtc >= before.SplitUtc
            ? r with { EndUsed = r.EndUsed + 50, LowerDelta = r.LowerDelta + 50, UpperDelta = r.UpperDelta + 50 } : r).ToArray());
        Assert.Equal(before.SplitUtc, after.SplitUtc);
        foreach (var model in before.Groups.Single().Models)
        {
            var same = after.Groups.Single().Models.Single(m => m.Model == model.Model && m.TrainingPolicy == model.TrainingPolicy);
            Assert.Equal(model.FiveToWeeklyScale, same.FiveToWeeklyScale); Assert.Equal(model.WeeklyToFiveScale, same.WeeklyToFiveScale);
            Assert.Equal(JsonSerializer.Serialize(model.Training), JsonSerializer.Serialize(same.Training));
            Assert.Equal(JsonSerializer.Serialize(model.Scores.Where(s => s.Period == "development")), JsonSerializer.Serialize(same.Scores.Where(s => s.Period == "development")));
        }
    }

    [Fact]
    public void AccountsPlansBucketsAndEndpointClocksDoNotFuzzyMatch()
    {
        foreach (var change in new Func<QuotaCostObservation, QuotaCostObservation>[]
        {
            r => r with { Cohort = r.Cohort with { AccountKey = "other" } },
            r => r with { Cohort = r.Cohort with { PlanType = "other" } },
            r => r with { Cohort = r.Cohort with { LimitId = "other" } },
            r => r with { EndUtc = r.EndUtc.AddSeconds(1) },
        }) Assert.Equal(0, Run(Rows().Select(r => r.Cohort.Kind == QuotaWindowKind.Weekly ? change(r) : r).ToArray()).AlignedIntervals);
        var differentWork = Rows().Select(r => r.Cohort.Kind == QuotaWindowKind.Weekly ? r with { Features = r.Features with { Tokens = 1 } } : r).ToArray();
        Assert.Equal(100, Run(differentWork).Unpaired["different-recorded-workload"]);
    }

    [Fact]
    public void FlatReferenceLateLabelsAndSparsePrefixesRemainUnsupported()
    {
        var flat = Rows().Select(r => r.Cohort.Kind == QuotaWindowKind.Weekly ? r with { EndUsed = 0, LowerDelta = 0, UpperDelta = 0 } : r).ToArray();
        var model = Run(flat).Groups.Single().Models.Single(m => m.Model == "categories" && m.TrainingPolicy == "development-prefix");
        Assert.Null(model.WeeklyToFiveScale); Assert.Contains("weekly-reference-has-no-scale-support", model.Qualifications);
        Assert.All(model.Scores.Where(s => s.Window == "FiveHour"), s => Assert.Equal(0, s.Matched));
        var late = Rows().Select(r => r with { TokenCostEvidenceAvailableAtUtc = Start.AddDays(100) }).ToArray();
        Assert.Equal(0, Run(late).AlignedIntervals);
        Assert.NotEmpty(QuotaTransferEvaluator.EvaluateAligned(late, Start, Start.AddHours(600), ForecastReplayAvailability.ReconstructedEventTime).Groups);
        Assert.All(Run(Rows(count: 10)).Groups.Single().Models, m => Assert.Equal("insufficient-aligned-development-training", m.Status));
    }

    [Fact]
    public void DevelopmentReferenceCanUseLaterAvailableWorkWithoutErasingQuietTraining()
    {
        var empty = new CodexJointWorkload(CodexJointWorkload.Version, CodexJointQuantityKind.ObservedIncrements, [], null, 0, 0);
        var rows = Rows().Select(r => r.StartUtc < Start.AddHours(120) ? r with
            { Features = r.Features with { Tokens = 0, JointWorkload = empty }, TokenCategories = [0, 0, 0, 0, 0] } : r).ToArray();
        var models = Run(rows).Groups.Single().Models;
        Assert.All(models.Where(m => m.TrainingPolicy == "first20"), m => Assert.Equal("no-recorded-training-work", m.Status));
        Assert.All(models.Where(m => m.TrainingPolicy == "development-prefix"), m =>
        {
            Assert.Equal("research-only", m.Status); Assert.Equal(80, m.TrainingRows);
            Assert.Equal(20, m.Training.Count(r => r.Tokens == 0));
            Assert.All(m.Trials, t => Assert.True(t.OriginUtc >= Start.AddHours(480)));
            Assert.All(m.Scores.Where(s => s.Period == "development"), s => Assert.Equal(0, s.Origins));
        });
    }

    [Fact]
    public void MarkerSeparatesMissingSparseQualifiedAndCandidateEvidence()
    {
        var selection = new CodexSelection(Start, Start.AddHours(600));
        Assert.Equal("not-assessed", CodexRegimeMarkerProjection.Build(selection, null, null).State);
        Assert.Equal("filtered", CodexRegimeMarkerProjection.Build(selection with { Model = "model" }, null, null).State);
        var rows = Rows(); var cost = QuotaAccountingModel.Evaluate(rows, "Synthetic marker");
        var empty = new CodexRegimeReport(CodexRegimeModel.Version, [], CodexRegimeModel.Methodology);
        Assert.Equal("no-candidate", CodexRegimeMarkerProjection.Build(selection, cost, empty).State);
        var reference = cost.Scores.First(); var candidate = new CodexRegimeCandidate(reference.Cohort, .5, Start.AddHours(300), Start.AddHours(306), 0, 5, 2, 8, 8, "Synthetic");
        var regime = empty with { Candidates = [candidate] };
        string before = JsonSerializer.Serialize(cost);
        var marker = CodexRegimeMarkerProjection.Build(selection, cost, regime, "reference-id");
        Assert.Equal("candidate", marker.State); Assert.Contains("reference-id", marker.Detail); Assert.Contains("TT ruler remains separate", marker.Detail);
        Assert.DoesNotContain("private-account", marker.Detail); Assert.Equal(before, JsonSerializer.Serialize(cost));
        Assert.Equal("insufficient", CodexRegimeMarkerProjection.Build(selection with { AccountKey = "other" }, cost, regime).State);
        Assert.Equal("insufficient", CodexRegimeMarkerProjection.Build(selection with { ToUtc = Start.AddHours(12) }, cost, regime).State);
        Assert.Equal("insufficient", CodexRegimeMarkerProjection.Build(selection, cost with { Scores = [] }, regime).State);
    }
}
