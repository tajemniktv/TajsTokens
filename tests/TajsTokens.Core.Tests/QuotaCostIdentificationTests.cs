using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaCostIdentificationTests
{
    internal static QuotaCostObservation Work(QuotaCostObservation row, params (string? Model, int Category, long Tokens)[] cells)
    {
        var events=cells.Select((x,i)=>new CodexPredictiveTokenEvent("session-"+i,row.StartUtc.AddMinutes(1),row.EndUtc,x.Model,null,
            x.Category==0?x.Tokens:0,x.Category==1?x.Tokens:0,x.Category==2?x.Tokens:0,x.Category==3?x.Tokens:0,x.Category==4?x.Tokens:0,x.Tokens)).ToArray();
        var joint=CodexJointWorkloadBuilder.Build(events);
        return row with {TokenCategories=Enumerable.Range(0,5).Select(c=>(double)cells.Where(x=>x.Category==c).Sum(x=>x.Tokens)).ToArray(),
            Features=row.Features with {Tokens=cells.Sum(x=>x.Tokens),JointWorkload=joint}};
    }
    private static QuotaIdentificationCohort Analyze(params QuotaCostObservation[] rows)=>Assert.Single(QuotaCostIdentification.Analyze(rows).Cohorts);

    [Fact]
    public void ReplicatingOneMixtureAddsRowsButDoesNotIdentifyItsIndividualWeights()
    {
        var rows=QuotaCostEvaluationTests.Observations(100).Select((row,i)=>Work(row,("A",0,1_000_000*(i%3+1)),("B",3,1_000_000*(i%3+1)))).ToArray();
        var report=Analyze(rows);
        Assert.Equal(100,report.Observations); Assert.Equal(1,report.RetainedJoint.NumericalRank);
        Assert.Equal(2,report.RetainedJoint.ActiveColumns); Assert.Equal(1,report.RetainedJoint.ParticipationDimension!.Value,10);
        Assert.Equal(1,report.RetainedJoint.MaximumColumnCosine!.Value,10);
        Assert.Equal(80,report.LaterScenarios.WithinTrainingSpan); Assert.Equal(0,report.LaterScenarios.NovelCoordinates);
        Assert.True(report.ChronologicalSupport.EvaluationBlocks<report.Observations);
    }

    [Fact]
    public void EqualMarginalsDoNotEraseJointContrast()
    {
        var rows=QuotaCostEvaluationTests.Observations(2);
        var report=Analyze(Work(rows[0],("A",0,1_000_000),("B",3,1_000_000)),Work(rows[1],("A",3,1_000_000),("B",0,1_000_000)));
        Assert.Equal(2,report.FrozenFirst20Joint.NumericalRank); Assert.Equal(1,report.FrozenFirst20Marginals.NumericalRank);
        Assert.Equal(4,report.FrozenFirst20Joint.ActiveColumns);
    }

    [Fact]
    public void FamiliarMixturesAndVolumeEditsDifferFromNovelJointDirections()
    {
        var rows=QuotaCostEvaluationTests.Observations(24).Select(row=>Work(row,("A",0,1_000_000),("B",3,1_000_000))).ToArray();
        rows[20]=Work(rows[20],("A",0,2_000_000),("B",3,2_000_000));
        rows[21]=Work(rows[21],("A",0,1_500_000),("B",3,500_000));
        rows[22]=Work(rows[22],("C",0,2_000_000)); rows[23]=Work(rows[23]);
        var report=Analyze(rows);
        Assert.Equal(1,report.FrozenFirst20Joint.NumericalRank); Assert.Equal(3,report.RetainedJoint.NumericalRank);
        Assert.Equal(1,report.LaterScenarios.WithinTrainingSpan); Assert.Equal(2,report.LaterScenarios.OutsideTrainingSpan);
        Assert.Equal(1,report.LaterScenarios.NovelCoordinates); Assert.Equal(1,report.LaterScenarios.OutsideTokenRange);
        Assert.Equal(1,report.LaterScenarios.ZeroWorkRows);
    }

    [Fact]
    public void MissingInvalidAndZeroRowsDoNotBecomeIdentifiedFreeWork()
    {
        var rows=QuotaCostEvaluationTests.Observations(3);
        rows[0]=rows[0] with {Features=rows[0].Features with {JointWorkload=null}};
        rows[1]=Work(rows[1],("A",0,100)); rows[1]=rows[1] with {TokenCategories=[0,0,0,0,100]};
        rows[2]=Work(rows[2]);
        var report=Analyze(rows);
        Assert.Equal(1,report.MissingJointRows); Assert.Equal(1,report.InvalidJointRows);
        Assert.Equal(1,report.ZeroWorkRows); Assert.Equal(0,report.RetainedJoint.NumericalRank);
        Assert.Equal(1,report.ZeroWorkPositiveMovementRows); Assert.Equal(1,report.ZeroWorkPositiveEnvelopeRows);
        Assert.Null(report.RetainedJoint.ParticipationDimension);
    }

    [Fact]
    public void MissingLabelsStaySeparateFromVocabularyOverflowAndCohortsStaySeparate()
    {
        var baseRows=QuotaCostEvaluationTests.Observations(2);
        var unknown=Work(baseRows[0],(null,0,100));
        var overflow=Work(baseRows[1],Enumerable.Range(0,257).Select(i=>((string?)$"model-{i:D3}",0,100L)).ToArray());
        var report=Analyze(unknown,overflow);
        Assert.Equal(100,report.MissingModelTokens); Assert.Equal(100,report.OverflowTokens);
        Assert.Equal(0,report.InvalidJointRows);
        var separated=QuotaCostIdentification.Analyze([unknown,overflow with {Cohort=overflow.Cohort with {AccountKey="other"}}]);
        Assert.Equal(2,separated.Cohorts.Count);
    }

    [Fact]
    public void DiagnosticBoundsAndCancellationAreExplicit()
    {
        var rows=QuotaCostEvaluationTests.Observations(QuotaCostIdentification.MaximumRows+1).Select(row=>Work(row,("A",0,1))).ToArray();
        var report=Analyze(rows);
        Assert.Equal(QuotaCostIdentification.MaximumRows+1,report.Observations);
        Assert.Equal(QuotaCostIdentification.MaximumRows,report.ExaminedRows);
        using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(()=>QuotaCostIdentification.Analyze(rows,cancellation.Token));
    }

    [Fact]
    public void RankAndParticipationAreInvariantToCoordinateUnitsAndWholeDatasetReplication()
    {
        var rows=QuotaCostEvaluationTests.Observations(2);
        var first=Analyze(Work(rows[0],("A",0,1)),Work(rows[1],("B",3,1_000_000_000)));
        var other=Analyze(Work(rows[0],("A",0,1_000_000_000)),Work(rows[1],("B",3,1)),
            Work(rows[0],("A",0,1_000_000_000)),Work(rows[1],("B",3,1)));
        Assert.Equal(2,first.RetainedJoint.NumericalRank); Assert.Equal(first.RetainedJoint.NumericalRank,other.RetainedJoint.NumericalRank);
        Assert.Equal(2,first.RetainedJoint.ParticipationDimension!.Value,10);
        Assert.Equal(first.RetainedJoint.ParticipationDimension.Value,other.RetainedJoint.ParticipationDimension!.Value,10);
    }
}
