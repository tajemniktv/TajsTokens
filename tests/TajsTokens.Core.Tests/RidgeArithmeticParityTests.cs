using System.Security.Cryptography;
using System.Text;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class RidgeArithmeticParityTests
{
    [Fact]
    public void OptimizedComputeBuildPreservesCapturedFitBitsIncludingSignedZeroAndInvalidArithmetic()
    {
        // Golden digest captured from the original Debug Core assembly's dense solver.
        // Include zero/constant columns, singular/negative penalties and extreme finite inputs.
        var random = new Random(7521);
        var cases = new List<(double[][] X, double[] Y, double Penalty)>();
        for (int k = 0; k < 240; k++)
        {
            int n = 1 + k % 120, width = k % 39;
            var x = Enumerable.Range(0, n).Select(i => Enumerable.Range(0, width)
                .Select(j => j % 7 == 0 ? k * .1 : random.NextDouble() < .65 ? 0 : random.NextDouble() * 20 - 10).ToArray()).ToArray();
            var y = Enumerable.Range(0, n).Select(i => random.NextDouble() * 100).ToArray();
            cases.Add((x, y, k % 5 == 0 ? 0 : k % 7 == 0 ? -1 : 10));
        }
        foreach (double value in new[] { 0d, -0d, 1e-300, 1e150, 1e300, double.MaxValue, double.NaN, double.PositiveInfinity })
        foreach (double penalty in new[] { 0d, 1d, -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            cases.Add(([[value, 0], [value, 1], [value, 2]], [1, 2, 3], penalty));

        var golden = new StringBuilder();
        foreach (var c in cases)
        {
            var fit = AccountLocalRidge.Fit(c.X, c.Y, c.Penalty);
            string hash = fit is null ? "null" : Convert.ToHexStringLower(SHA256.HashData(
                fit.Means.Concat(fit.Scales).Concat(fit.Coefficients)
                    .Select(BitConverter.DoubleToInt64Bits).SelectMany(BitConverter.GetBytes).ToArray()));
            golden.Append(hash).Append('\n');
        }
        Assert.Equal("b43cb99aea20c7f1ada8fad16335e14d5ea5a9a08775cec9ba602a2b93989234",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(golden.ToString()))));
    }
}
