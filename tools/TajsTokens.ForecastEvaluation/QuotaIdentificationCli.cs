using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Services;

internal static class QuotaIdentificationCli
{
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
