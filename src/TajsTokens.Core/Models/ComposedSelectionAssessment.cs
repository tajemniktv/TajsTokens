// Taj's Tokens | ComposedSelectionAssessment.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public sealed record ComposedSelectionAssessment(
    bool Passed,
    bool RequiresStrictReplay,
    int Outcomes,
    int EvaluationBlocks,
    int IncumbentPairs,
    double? BlockBalancedLoss,
    double? BlockBalancedIncumbentLoss,
    double? BlockBalancedPaceLoss,
    IReadOnlyList<string> Reasons)
{
    public string Explanation =>
        (Passed ? "Selection evidence gate passed. " : "Selection evidence gate not passed. ") +
        $"{Outcomes} outcomes across {EvaluationBlocks} chronological blocks; {IncumbentPairs} incumbent pairs. " +
        string.Join(" ", Reasons) +
        " This is not a live promotion decision: cost-model support, paired raw-token comparison, current account/composition, " +
        "fresh training and completed-block uncertainty calibration are checked separately. The incumbent remains until all gates pass.";
}