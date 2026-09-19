namespace TajsTokens.Core.Models;

public sealed record ComposedSelectionAssessment(bool Passed, bool RequiresStrictReplay, int Outcomes,
    int ResetGenerations, int IncumbentPairs, double? ResetBalancedLoss,
    double? ResetBalancedIncumbentLoss, double? ResetBalancedPaceLoss, IReadOnlyList<string> Reasons)
{
    public string Explanation =>
        (Passed ? "Selection evidence gate passed. " : "Selection evidence gate not passed. ") +
        $"{Outcomes} outcomes across {ResetGenerations} independent reset groups; {IncumbentPairs} incumbent pairs. " +
        string.Join(" ", Reasons) +
        " This is not a live promotion decision: cost-model support, paired raw-token comparison, current account/composition, " +
        "fresh training and completed-reset uncertainty calibration are checked separately. The incumbent remains until all gates pass.";
}
