namespace TajsTokens.Core.Interfaces;

/// <summary>Rebuildable numeric fit artifacts; keys include every training value and solver policy.</summary>
public interface IRidgeFitCache
{
    RidgeFitParameters? Read(string key);
    void Write(string key, RidgeFitParameters fit);
}

public sealed record RidgeFitParameters(double[] Means, double[] Scales, double[] Coefficients);
