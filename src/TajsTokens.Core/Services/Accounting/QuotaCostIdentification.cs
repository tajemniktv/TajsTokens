using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Bounded retrospective design diagnostics. Does not fit quota prices or change scenario eligibility.</summary>
public static class QuotaCostIdentification
{
    public const string Version = "quota-cost-identification/v1";
    public const int MaximumRows = 2048;
    public const double RankTolerance = 1e-8;
    public const string Methodology = "Retrospective model/category design, separate by compatible cohort and horizon. " +
        "At most the first 2048 intervals per cohort are examined; counts expose truncation. Vocabulary is training-only, capped at 16 models; missing labels and vocabulary overflow have separate category bins. " +
        "Frozen first-20 is the existing comparator, not a sample-size theorem. Numerical rank uses pivoted, twice-reorthogonalized row Gram-Schmidt after noncentered column RMS scaling, relative residual tolerance 1e-8. " +
        "Participation dimension is trace(G)^2/trace(G^2) for the column-normalized Gram matrix; it is not entropy rank or an independent sample count. " +
        "Later row-space residual <=1e-6, token-range and duration checks describe geometry only; they do not establish feasible nonnegative prices, predictive calibration or account attribution. " +
        "Six-hour blocks and effective sample size reuse chronological-blocks/v1 assumptions, not proof of independence. Reset generations are observed groups, not automatically complete cycles. " +
        "Missing local activity is not zero account cost. Effort/tier departures are not identified by this diagnostic. No model is promoted.";

    public static QuotaCostIdentificationReport Analyze(IReadOnlyList<QuotaCostObservation> observations, CancellationToken token = default,
        Action<int, string, double[][], QuotaDesignInformation>? numericalAudit = null)
    {
        var result = new List<QuotaIdentificationCohort>();
        foreach (var group in observations.GroupBy(x => (x.Cohort, x.HorizonHours)))
        {
            token.ThrowIfCancellationRequested();
            var all = group.OrderBy(x => x.StartUtc).ThenBy(x => x.EndUtc).ToArray();
            var rows = all.Take(MaximumRows).ToArray();
            var first = rows.Take(20).ToArray();
            var validFirst = first.Where(Valid).ToArray();
            var valid = rows.Where(Valid).ToArray();
            var frozen = new Design(validFirst, true, token);
            var retained = new Design(valid, true, token);
            var marginal = new Design(validFirst, false, token);
            numericalAudit?.Invoke(result.Count, "frozen-first20-joint", frozen.Matrix, frozen.Information);
            numericalAudit?.Invoke(result.Count, "retained-joint", retained.Matrix, retained.Information);
            numericalAudit?.Invoke(result.Count, "frozen-first20-marginals", marginal.Matrix, marginal.Information);
            int missing = 0, zero = 0, within = 0, outside = 0, novel = 0, amount = 0, duration = 0;
            var later = rows.Skip(first.Length).ToArray();
            foreach (var row in later)
            {
                if (!Valid(row)) { missing++; continue; }
                if (row.Features.Tokens == 0) { zero++; continue; }
                double[] vector = frozen.Values(row);
                double norm = Norm(vector); double[] residual = (double[])vector.Clone();
                ProjectAway(residual, frozen.Basis);
                if (frozen.Basis.Count > 0 && Norm(residual) / norm <= 1e-6) within++; else outside++;
                if (vector.Where((x, i) => x > 0 && !frozen.Active[i]).Any()) novel++;
                if (validFirst.Length == 0 || row.Features.Tokens < validFirst.Min(x => x.Features.Tokens) || row.Features.Tokens > validFirst.Max(x => x.Features.Tokens)) amount++;
                if (validFirst.Length == 0 || row.EndUtc-row.StartUtc < validFirst.Min(x => x.EndUtc-x.StartUtc) || row.EndUtc-row.StartUtc > validFirst.Max(x => x.EndUtc-x.StartUtc)) duration++;
            }
            result.Add(new(group.Key.Cohort, group.Key.HorizonHours, all.Length, rows.Length,
                rows.Count(x => x.Features.JointWorkload is null), rows.Count(x => x.Features.JointWorkload is not null && !Valid(x)),
                rows.Count(x => x.Features.Tokens == 0), rows.Count(x => x.Features.Tokens == 0 && x.ObservedDelta > 0),
                rows.Count(x => x.Features.Tokens == 0 && x.LowerDelta > 0),
                rows.Count(x => x.TokenCostEvidenceAvailableAtUtc is null || x.TokenCostEvidenceAvailableAtUtc > x.EndUtc),
                rows.Sum(x => (decimal)Math.Max(0, x.Features.Tokens)),
                rows.Sum(x => x.Features.JointWorkload?.MissingModelTokens ?? 0), rows.Sum(x => x.Features.JointWorkload?.Overflow?.ReportedTotal ?? 0),
                frozen.Information, retained.Information, marginal.Information,
                new(later.Length, missing, zero, within, outside, novel, amount, duration),
                ChronologicalEvidence.Describe(rows, x => x.StartUtc, x => x.EndUtc, x => x.ResetUtc, x => x.ObservedDelta)));
        }
        return new(Version, Methodology, result);
    }

    internal static bool Valid(QuotaCostObservation row) => row.HasCompleteTokenCategories &&
        row.Features.JointWorkload is { Kind: CodexJointQuantityKind.ObservedIncrements } joint &&
        joint.ReportedTokens == row.Features.Tokens && joint.MissingModelTokens >= 0 && joint.MissingModelTokens <= joint.ReportedTokens &&
        joint.Cells.All(x => x.IsComplete) && (joint.Overflow is null || joint.Overflow.IsComplete) &&
        Enumerable.Range(0,5).All(i => Math.Abs((double)(joint.Cells.Sum(x => TtJointWorkloadBasis.Amount(x,(TtTokenCategory)i)) +
            (joint.Overflow is { } overflow ? TtJointWorkloadBasis.Amount(overflow,(TtTokenCategory)i) : 0)) - row.TokenCategories[i]) < .001);

    private sealed class Design
    {
        private readonly string[] _models;
        private readonly bool _joint;
        private readonly double[] _scales;
        public bool[] Active { get; }
        public IReadOnlyList<double[]> Basis { get; }
        public QuotaDesignInformation Information { get; }
        public double[][] Matrix { get; }
        public Design(QuotaCostObservation[] rows, bool joint, CancellationToken token)
        {
            _joint = joint;
            _models = rows.SelectMany(x => x.Features.JointWorkload!.Cells).Select(x => x.Coordinate!.Model)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(16).Cast<string>().ToArray();
            string[] names = (joint ? _models.Concat(["(missing-model)", "(vocabulary-overflow)"])
                .SelectMany(x => Enumerable.Range(0,5).Select(i => $"{x}/{(TtTokenCategory)i}"))
                : Enumerable.Range(0,5).Select(i => $"pooled/{(TtTokenCategory)i}").Concat(_models.Select(x => "model/" + x)).Concat(["missing-model", "vocabulary-overflow"])).ToArray();
            _scales = Enumerable.Repeat(1d, names.Length).ToArray();
            double[][] raw = rows.Select(Values).ToArray();
            _scales = Enumerable.Range(0,names.Length).Select(i => raw.Length == 0 ? 1 : Math.Max(1e-12, Math.Sqrt(raw.Average(x => x[i]*x[i])))).ToArray();
            double[][] matrix = rows.Select(Values).ToArray();
            Matrix = matrix;
            Active = Enumerable.Range(0,names.Length).Select(i => raw.Any(x => x[i] > 0)).ToArray();
            Basis = Span(matrix, token);
            double[][] columns = Enumerable.Range(0,names.Length).Where(i => Active[i]).Select(i =>
            {
                double[] column = matrix.Select(x => x[i]).ToArray(); double norm = Norm(column);
                return column.Select(x => x/norm).ToArray();
            }).ToArray();
            double squareTrace = columns.Length; double? maximum = null;
            for (int i=0;i<columns.Length;i++) for(int j=0;j<i;j++)
            {
                double dot=Dot(columns[i],columns[j]); squareTrace+=2*dot*dot;
                maximum=Math.Max(maximum ?? 0,Math.Min(1,Math.Abs(dot)));
            }
            Information = new(rows.Length,names.Length,columns.Length,Basis.Count,
                squareTrace == 0 ? null : columns.Length*(double)columns.Length/squareTrace,maximum,
                names.Where((_,i)=>!Active[i]).ToArray());
        }
        public double[] Values(QuotaCostObservation row)
        {
            double[] result = new double[_scales.Length];
            var workload = row.Features.JointWorkload!;
            foreach (var cell in workload.Cells.Concat(workload.Overflow is { } overflow ? [overflow] : []))
            {
                string? model = cell.Coordinate?.Model;
                int m = string.IsNullOrWhiteSpace(model) && cell.Coordinate is not null ? _models.Length : Array.IndexOf(_models, model);
                if (m < 0) m = _models.Length+1;
                for(int c=0;c<5;c++)
                {
                    double value=(double)TtJointWorkloadBasis.Amount(cell,(TtTokenCategory)c)/1e6;
                    if(_joint) result[m*5+c]+=value;
                    else { result[c]+=value; result[5+m]+=value; }
                }
            }
            return result.Select((x,i)=>x/_scales[i]).ToArray();
        }
    }
    private static IReadOnlyList<double[]> Span(double[][] rows, CancellationToken token)
    {
        var remaining = rows.Where(x=>Norm(x)>0).Select(x=> { double norm=Norm(x); return x.Select(v=>v/norm).ToArray(); }).ToList();
        var basis = new List<double[]>();
        while(remaining.Count>0)
        {
            token.ThrowIfCancellationRequested();
            int best=0; for(int i=1;i<remaining.Count;i++) if(Dot(remaining[i],remaining[i])>Dot(remaining[best],remaining[best])) best=i;
            if(Norm(remaining[best])<=RankTolerance) break;
            double[] next=remaining[best]; remaining.RemoveAt(best); ProjectAway(next,basis);
            double norm=Norm(next); if(norm<=RankTolerance) continue;
            double[] unit=next.Select(x=>x/norm).ToArray(); basis.Add(unit);
            foreach(double[] row in remaining) ProjectAway(row,[unit]);
            if(basis.Count==unit.Length) break;
        }
        return basis;
    }
    private static void ProjectAway(double[] row, IReadOnlyList<double[]> basis)
    {
        for(int pass=0;pass<2;pass++) foreach(double[] unit in basis)
        { double dot=Dot(row,unit); for(int i=0;i<row.Length;i++) row[i]-=dot*unit[i]; }
    }
    private static double Dot(double[] a,double[] b) { double sum=0; for(int i=0;i<a.Length;i++) sum+=a[i]*b[i]; return sum; }
    private static double Norm(double[] row)=>Math.Sqrt(Dot(row,row));
}
