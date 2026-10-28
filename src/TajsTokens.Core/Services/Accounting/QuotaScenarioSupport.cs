using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Small explicit set-membership prototype, outside live prediction/selection.</summary>
public static class QuotaScenarioSupport
{
    public const string Version = "quota-scenario-support/v1";
    public const int MaximumConstraints = 20;
    public const int MaximumActiveVariables = 4;
    public const int MaximumVertexSystems = 200_000;
    public const double NumericalTolerance = 1e-8;
    public const string Methodology = "Conditional local-work identification range, not a confidence interval, prediction interval or account-wide quota forecast. " +
        "Nonnegative token-only linear model/category coefficients, constant across the supplied compatible intervals. " +
        "Caller explicitly supplies nonnegative external-activity upper bound (null means unrestricted), lag and mismatch allowances in quota percentage points per interval. " +
        "Local bounds are max(0, lower-external-lag-mismatch) and upper+lag+mismatch; unrestricted external activity removes the positive local lower bound. " +
        "Measurement envelopes retain their supplied precision policy; assumptions are sensitivity inputs, not measured lag or proven ownership. " +
        "No effort/tier multiplier or fitted intercept. Complete known-model joint quantities required. " +
        "At most 20 constraints, 4 active coordinates and 200000 vertex systems; no silent truncation, prior or tolerance widening. " +
        "Column/row-scaled vertex enumeration uses declared 1e-8 relative feasibility tolerance; endpoints are numerical approximations, not certified exact arithmetic. " +
        "Ill-conditioned systems return numerically uncertain. Infeasibility applies to the declared assumptions; unboundedness is preserved. No live promotion.";

    public static IReadOnlyList<QuotaCostScenarioDiagnostic> Diagnose(IReadOnlyList<QuotaCostObservation> observations, CancellationToken token = default)
    {
        var results = new List<QuotaCostScenarioDiagnostic>();
        foreach (var group in observations.GroupBy(x => (x.Cohort, x.HorizonHours)))
        {
            token.ThrowIfCancellationRequested();
            var ordered = group.OrderBy(x => x.StartUtc).ToArray();
            var training = ordered.Take(MaximumConstraints).ToArray();
            var familiar = training.FirstOrDefault(x => x.Features.Tokens > 0 && x.Features.JointWorkload is not null);
            var latest = ordered.LastOrDefault(x => x.Features.Tokens > 0 && x.Features.JointWorkload is not null);
            foreach (var (label, row) in new[] { ("first-training-mixture", familiar), ("latest-retained-mixture", latest) })
            {
                if (row?.Features.JointWorkload is not { } scenario) continue;
                foreach (var (name, assumption) in new[]
                {
                    ("unrestricted-external-zero-lag-sensitivity", new QuotaCostSupportAssumptions(null, 0, 0)),
                    ("complete-local-zero-lag-counterfactual", new QuotaCostSupportAssumptions(0, 0, 0)),
                })
                    results.Add(new(group.Key.Cohort, group.Key.HorizonHours, label, row.EndUtc, name,
                        Analyze(training, group.Key.Cohort, group.Key.HorizonHours, scenario, assumption, token)));
            }
        }
        return results;
    }

    public static QuotaScenarioSupportResult Analyze(IReadOnlyList<QuotaCostObservation> rows,
        QuotaHistoryCohort cohort, double horizonHours, CodexJointWorkload scenario,
        QuotaCostSupportAssumptions assumptions, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        int dimensions = 0, systems = 0, vertices = 0;
        string[] unobserved = [];
        QuotaScenarioSupportResult Result(QuotaScenarioSupportStatus status, string reason, double? low = null, double? high = null) =>
            new(Version, status, low, high, rows.Count, dimensions, systems, vertices, unobserved,
                rows.Select(x => x.PrecisionPolicy).Distinct(StringComparer.Ordinal).ToArray(), assumptions, reason, Methodology);

        bool Nonnegative(double value) => double.IsFinite(value) && value >= 0;
        if (assumptions is null || !Nonnegative(assumptions.LagAllowance) || !Nonnegative(assumptions.ModelMismatchAllowance) ||
            assumptions.ExternalActivityUpperBound is { } external && !Nonnegative(external))
            throw new ArgumentException("Declare finite nonnegative support assumptions; null external upper bound means unrestricted.");
        if (!double.IsFinite(horizonHours) || horizonHours <= 0 || rows.Count == 0)
            return Result(QuotaScenarioSupportStatus.Unsupported, "missing-compatible-constraints");
        if (rows.Any(x => x.Cohort != cohort || x.HorizonHours != horizonHours))
            return Result(QuotaScenarioSupportStatus.Unsupported, "mixed-scope-or-horizon");
        if (rows.Any(x => !QuotaCostIdentification.Valid(x) || !double.IsFinite(x.LowerDelta) || !double.IsFinite(x.UpperDelta) || x.LowerDelta > x.UpperDelta))
            return Result(QuotaScenarioSupportStatus.Unsupported, "incomplete-joint-or-invalid-envelope");
        bool Known(CodexJointWorkload work) => work.IsComplete && work.MissingModelTokens == 0 &&
            (work.Overflow?.ReportedTotal ?? 0) == 0 && work.Cells.All(x => x.ReportedTotal == 0 || !string.IsNullOrWhiteSpace(x.Coordinate!.Model));
        if (scenario is null || !Known(scenario) || rows.Any(x => !Known(x.Features.JointWorkload!)))
            return Result(QuotaScenarioSupportStatus.Unsupported, "unknown-model-or-coordinate-overflow");
        if (rows.Count > MaximumConstraints)
            return Result(QuotaScenarioSupportStatus.Unsupported, "research-constraint-bound");
        if (rows.SelectMany(x => x.Features.JointWorkload!.Cells).Concat(scenario.Cells).Select(x => x.Coordinate!.Model)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Count() > 16)
            return Result(QuotaScenarioSupportStatus.Unsupported, "research-model-vocabulary-bound");

        var design = QuotaAccountingModel.JointScenarioDesign(rows, scenario);
        double[] lower = rows.Select(x => assumptions.ExternalActivityUpperBound is { } bound
            ? Math.Max(0, x.LowerDelta - bound - assumptions.LagAllowance - assumptions.ModelMismatchAllowance) : 0).ToArray();
        double[] upper = rows.Select(x => x.UpperDelta + assumptions.LagAllowance + assumptions.ModelMismatchAllowance).ToArray();
        if (upper.Any(x => !double.IsFinite(x)) || lower.Any(x => !double.IsFinite(x)))
            return Result(QuotaScenarioSupportStatus.Unsupported, "nonfinite-adjusted-envelope");
        if (upper.Any(x => x < 0) || Enumerable.Range(0, rows.Count).Any(i => design.Training[i].All(x => x == 0) && lower[i] > 0))
            return Result(QuotaScenarioSupportStatus.Infeasible, "zero-local-work-conflicts-with-declared-envelope");

        int[] active = Enumerable.Range(0, design.Names.Length).Where(i => design.Training.Any(x => x[i] > 0)).ToArray();
        dimensions = active.Length;
        unobserved = Enumerable.Range(0, design.Names.Length).Where(i => design.Scenario[i] > 0 && !active.Contains(i)).Select(i => design.Names[i]).ToArray();
        if (dimensions > MaximumActiveVariables)
            return Result(QuotaScenarioSupportStatus.Unsupported, "research-dimension-bound");
        if (dimensions == 0)
            return unobserved.Length > 0 ? Result(QuotaScenarioSupportStatus.Unbounded, "unobserved-requested-coordinates", 0)
                : Result(QuotaScenarioSupportStatus.Bounded, "zero-requested-local-work", 0, 0);

        double[] scales = active.Select(i => design.Training.Max(x => x[i])).ToArray();
        double[] objective = active.Select((i, j) => design.Scenario[i] / scales[j]).ToArray();
        var inequalities = new List<(double[] A, double B)>();
        void Add(double[] a, double b)
        {
            double scale = a.Max(Math.Abs);
            if (scale == 0) return;
            a = a.Select(x => x / scale).ToArray(); b /= scale;
            int existing = inequalities.FindIndex(x => x.A.SequenceEqual(a));
            if (existing < 0) inequalities.Add((a, b));
            else if (b < inequalities[existing].B) inequalities[existing] = (a, b);
        }
        for (int r = 0; r < rows.Count; r++)
        {
            double[] a = active.Select((i, j) => design.Training[r][i] / scales[j]).ToArray();
            Add(a, upper[r]);
            if (lower[r] > 0) Add(a.Select(x => -x).ToArray(), -lower[r]);
        }
        for (int i = 0; i < dimensions; i++) { double[] a = new double[dimensions]; a[i] = -1; Add(a, 0); }
        if (objective.Any(x => !double.IsFinite(x)) || inequalities.Any(x => !double.IsFinite(x.B)))
            return Result(QuotaScenarioSupportStatus.NumericallyUncertain, "nonfinite-scaled-system");
        double combinations = 1;
        for (int i = 0; i < dimensions; i++) combinations *= (inequalities.Count - i) / (double)(i + 1);
        if (combinations > MaximumVertexSystems) return Result(QuotaScenarioSupportStatus.Unsupported, "research-vertex-bound");

        int[] selected = new int[dimensions];
        double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
        bool uncertain = false;
        void Enumerate(int depth, int start)
        {
            token.ThrowIfCancellationRequested();
            if (depth < dimensions)
            {
                for (int i = start; i <= inequalities.Count - (dimensions - depth); i++)
                { selected[depth] = i; Enumerate(depth + 1, i + 1); }
                return;
            }
            systems++;
            double[][] equations = selected.Select(i => inequalities[i].A.Append(inequalities[i].B).ToArray()).ToArray();
            double[]? point = Solve(equations, ref uncertain);
            if (point is null || inequalities.Any(x => Dot(x.A, point) > x.B + NumericalTolerance * Math.Max(1, Math.Abs(x.B)))) return;
            vertices++;
            double value = Math.Max(0, Dot(objective, point));
            if (!double.IsFinite(value)) { uncertain = true; return; }
            minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value);
        }
        Enumerate(0, 0);
        if (uncertain) return Result(QuotaScenarioSupportStatus.NumericallyUncertain, "ill-conditioned-vertex-system");
        if (vertices == 0) return Result(QuotaScenarioSupportStatus.Infeasible, "no-feasible-nonnegative-coefficients-at-declared-tolerance");
        return unobserved.Length > 0 ? Result(QuotaScenarioSupportStatus.Unbounded, "unobserved-requested-coordinates", minimum)
            : Result(QuotaScenarioSupportStatus.Bounded, "conditional-numerical-identification-range", minimum, maximum);
    }

    private static double[]? Solve(double[][] a, ref bool uncertain)
    {
        int n = a.Length;
        for (int k = 0; k < n; k++)
        {
            int pivot = k;
            for (int i = k + 1; i < n; i++) if (Math.Abs(a[i][k]) > Math.Abs(a[pivot][k])) pivot = i;
            double size = Math.Abs(a[pivot][k]);
            if (size == 0) return null;
            if (size < 1e-9) { uncertain = true; return null; }
            (a[k], a[pivot]) = (a[pivot], a[k]);
            double divisor = a[k][k];
            for (int j = k; j <= n; j++) a[k][j] /= divisor;
            for (int i = 0; i < n; i++) if (i != k)
            {
                double multiple = a[i][k];
                for (int j = k; j <= n; j++) a[i][j] -= multiple * a[k][j];
            }
        }
        double[] point = a.Select(row => row[n]).ToArray();
        if (point.Any(x => !double.IsFinite(x))) { uncertain = true; return null; }
        return point;
    }

    private static double Dot(double[] a, double[] b) => a.Select((x, i) => x * b[i]).Sum();
}
