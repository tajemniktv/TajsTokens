namespace TajsTokens.Core.Models;

public sealed record TokenBreakdown(
    double Input,
    double CachedInput,
    double Output,
    double Reasoning)
{
    public double Total => Input + CachedInput + Output + Reasoning;
}
