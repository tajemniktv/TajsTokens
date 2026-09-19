namespace TajsTokens.Core.Models;

/// <summary>Counterfactual workload weight and explicit coverage, not provider-reported money.</summary>
public sealed record ApiPriceWeight(string RateCardVersion, decimal WeightedAmount,
    decimal PricedReportedTokens, decimal UnpricedReportedTokens, int PricedEvents, int UnpricedEvents)
{
    public bool IsComplete => UnpricedEvents == 0;
}
