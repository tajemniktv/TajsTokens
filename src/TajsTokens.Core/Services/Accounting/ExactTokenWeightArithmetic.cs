using System.Numerics;

namespace TajsTokens.Core.Services;

/// <summary>Exact finite-decimal token/weight arithmetic shared by defined reference calculations.</summary>
internal static class ExactTokenWeightArithmetic
{
    private static readonly BigInteger MaximumMantissa = (BigInteger.One << 96) - 1;
    private static BigInteger Scaled(decimal value)
    {
        int[] bits = decimal.GetBits(value); int scale = (bits[3] >> 16) & 255;
        BigInteger n = (uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
        return ((bits[3] & int.MinValue) != 0 ? -n : n) * BigInteger.Pow(10, 28 - scale);
    }
    public static BigInteger Product(decimal amount, decimal weight) => Scaled(amount) * Scaled(weight);
    public static decimal ExactSum(IEnumerable<decimal> values)
    {
        BigInteger sum = values.Aggregate(BigInteger.Zero, (s, x) => s + Scaled(x));
        bool negative = sum.Sign < 0; sum = BigInteger.Abs(sum); int scale = 28;
        while (sum > MaximumMantissa && scale > 0)
        {
            sum = BigInteger.DivRem(sum, 10, out var remainder);
            if (remainder != 0) throw new OverflowException("Exact quantity sum exceeds decimal precision.");
            scale--;
        }
        if (sum > MaximumMantissa) throw new OverflowException();
        return Decimal(sum, scale, negative);
    }
    public static decimal Ratio(BigInteger numerator, BigInteger denominator, int direction)
    {
        for (int scale = 28; scale >= 0; scale--)
        {
            BigInteger n = numerator * BigInteger.Pow(10, scale);
            BigInteger q = BigInteger.DivRem(n, denominator, out var remainder);
            if (direction > 0 && remainder > 0 || direction == 0 &&
                (remainder * 2 > denominator || remainder * 2 == denominator && !q.IsEven)) q++;
            if (q > MaximumMantissa) continue;
            if (direction == 0 && numerator > 0 && q == 0) throw new OverflowException("Identified result is below decimal precision.");
            return Decimal(q, scale, false);
        }
        throw new OverflowException("Result exceeds decimal range.");
    }
    private static decimal Decimal(BigInteger value, int scale, bool negative) => new(
        unchecked((int)(uint)(value & uint.MaxValue)), unchecked((int)(uint)((value >> 32) & uint.MaxValue)),
        unchecked((int)(uint)((value >> 64) & uint.MaxValue)), negative, (byte)scale);
}
