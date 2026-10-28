using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Services;
using TajsTokens.Core.Models;

internal static class QuotaIdentificationCli
{
    public static async Task RunJointAsync(string[] args)
    {
        var from = DateTimeOffset.Parse(args[2], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
        var to = DateTimeOffset.Parse(args[3], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
        if (from >= to || to > DateTimeOffset.UtcNow || to - from > TimeSpan.FromDays(366)) throw new ArgumentException("Choose a past range up to 366 days.");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var data = await new SqliteForecastDatasetReader(args[1]).ReadAsync("codex", "default", from, to, cancellation.Token);
        var built = QuotaCostObservationBuilder.BuildDetailed(data, cancellation.Token, horizons: ComposedQuotaEvaluator.EvaluationHorizons);
        var cohorts = built.Observations.Select(x => x.Cohort).Distinct().ToList();
        var json = new JsonSerializerOptions { WriteIndented = false };
        if (args[0] == "--quota-support-study")
        {
            var results = QuotaScenarioSupport.Diagnose(built.Observations, cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { FromUtc = from, ToUtc = to, data.CapturedAtUtc, QuotaScenarioSupport.Version, QuotaScenarioSupport.Methodology,
                Cases = results.Select(x => new { Cohort = cohorts.IndexOf(x.Cohort), Window = x.Cohort.Kind.ToString(), x.HorizonHours,
                    x.Scenario, x.ScenarioObservedAtUtc, x.AssumptionCase, x.Result }) }, json));
        }
        else if (args[0] == "--joint-cost-study")
        {
            var report = QuotaAccountingModel.Evaluate(built.Observations, "Selected retained local intervals; not account-wide token attribution.", cancellation.Token,
                ["persistence", "pace", "total", "categories", "joint-categories"]);
            foreach (var group in built.Observations.GroupBy(x => (x.Cohort, x.HorizonHours)))
                Console.WriteLine(JsonSerializer.Serialize(new { RecordType = "numeric-observations", Cohort = cohorts.IndexOf(group.Key.Cohort), group.Key.HorizonHours,
                    Rows = group.OrderBy(x => x.StartUtc).Select(x => new { x.StartUtc, x.EndUtc, x.ResetUtc, x.LowerDelta, x.UpperDelta, x.ObservedDelta,
                        x.TokenCategories, x.Features.Tokens, Joint = x.Features.JointWorkload, x.TokenCostEvidenceAvailableAtUtc }) }, json));
            Console.WriteLine(JsonSerializer.Serialize(new { RecordType = "cost-report", FromUtc = from, ToUtc = to, data.CapturedAtUtc,
                QuotaCostEvaluation.Version, QuotaCostEvaluation.Methodology,
                Scores = report.Scores.Select(x => new { Cohort = cohorts.IndexOf(x.Cohort), Window = x.Cohort.Kind.ToString(), x.HorizonHours, x.Candidate,
                    x.Observations, x.TrainingSamples, x.HeldOutSamples, x.HeldOutGenerations, x.IntervalLoss, x.DisplayedDeltaMae, x.BlockMeanIntervalLoss,
                    x.PairedPaceIntervalLoss, x.PairedTotalIntervalLoss, x.PairedCategoryIntervalLoss, x.Status, x.MaterialWin, x.Coefficients,
                    x.IncompleteJointTrainingIntervals, x.IncompleteJointHeldOutIntervals, x.Trials, x.EvidenceSupport,
                    Uncertainty = QuotaUncertaintyDiagnosticsBuilder.Build(x.Trials) }) }, json));
        }
        else
        {
            var report = ComposedQuotaEvaluator.Evaluate(data, cancellation.Token,
                args[0] == "--joint-composed-strict" ? ForecastReplayAvailability.CollectedByOrigin : ForecastReplayAvailability.ReconstructedEventTime,
                includeJointResearch: true);
            Console.WriteLine(JsonSerializer.Serialize(new { RecordType = "composed-report", FromUtc = from, ToUtc = to, data.CapturedAtUtc,
                report.Version, report.Methodology,
                Scores = report.Scores.Select(x => new { Cohort = cohorts.IndexOf(x.Cohort), Window = x.Cohort.Kind.ToString(), x.HorizonHours, x.CostModel,
                    x.Availability, x.TrainingIntervals, x.HeldOutIntervals, x.ResetGenerations, x.MissingComposition, x.IntervalLoss, x.CostOnlyIntervalLoss,
                    x.PaceIntervalLoss, x.IncumbentIntervalLoss, x.DisplayedDeltaMae, x.WithheldReasons, x.EvidenceSupport,
                    Trials = x.Trials.Select(t => new { t.OriginUtc, t.OutcomeUtc, t.PredictedDelta, t.ActualWorkloadCost, t.ObservedDelta,
                        t.IntervalLoss, t.CostOnlyIntervalLoss, t.PaceIntervalLoss, t.IncumbentIntervalLoss }) }) }, json));
        }
    }

    public static async Task RunAsync(string[] args)
    {
        var from = DateTimeOffset.Parse(args[2],CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal).ToUniversalTime();
        var to = DateTimeOffset.Parse(args[3],CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal).ToUniversalTime();
        if(from>=to || to>DateTimeOffset.UtcNow || to-from>TimeSpan.FromDays(366)) throw new ArgumentException("Choose a past range up to 366 days.");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e)=>{e.Cancel=true;cancellation.Cancel();};
        var data=await new SqliteForecastDatasetReader(args[1]).ReadAsync("codex","default",from,to,cancellation.Token);
        var built=QuotaCostObservationBuilder.BuildDetailed(data,cancellation.Token,horizons:ComposedQuotaEvaluator.EvaluationHorizons);
        bool audit=args[0]=="--cost-identification-audit";
        var report=QuotaCostIdentification.Analyze(built.Observations,cancellation.Token,audit ?
            (cohort,design,matrix,information)=>Console.WriteLine(JsonSerializer.Serialize(new {RecordType="matrix",Cohort=cohort,Design=design,Matrix=matrix,Information=information})) : null);
        Console.WriteLine(JsonSerializer.Serialize(new {RecordType="report",FromUtc=from,ToUtc=to,data.CapturedAtUtc,report.Version,report.Methodology,
            Cohorts=report.Cohorts.Select((x,i)=>new {Cohort=i,Window=x.Cohort.Kind.ToString(),KnownAccount=x.Cohort.AccountKey is not null,
                x.HorizonHours,x.Observations,x.ExaminedRows,x.MissingJointRows,x.InvalidJointRows,x.ZeroWorkRows,
                x.ZeroWorkPositiveMovementRows,x.ZeroWorkPositiveEnvelopeRows,x.LateOrUnknownAvailabilityRows,
                x.ReportedTokens,x.MissingModelTokens,x.OverflowTokens,x.FrozenFirst20Joint,x.RetainedJoint,x.FrozenFirst20Marginals,x.LaterScenarios,x.ChronologicalSupport})},
            new JsonSerializerOptions{WriteIndented=!audit}));
    }
}
