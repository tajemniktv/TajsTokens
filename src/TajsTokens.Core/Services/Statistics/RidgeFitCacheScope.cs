using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using TajsTokens.Core.Interfaces;

namespace TajsTokens.Core.Services;

public static class RidgeFitCacheScope
{
    private static readonly AsyncLocal<IRidgeFitCache?> Active = new();
    internal static IRidgeFitCache? Current => Active.Value;

    public static IDisposable Use(IRidgeFitCache cache)
    {
        IRidgeFitCache? previous = Active.Value;
        Active.Value = cache;
        return new Scope(previous);
    }

    internal static string? Key(IReadOnlyList<double[]> features, IReadOnlyList<double> targets, double penalty)
    {
        long length = 24L + ((long)features.Count * features[0].Length + targets.Count) * 8;
        if (length > 1024 * 1024) return null;
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            int offset = 0;
            void Number(long value)
            {
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), value);
                offset += 8;
            }
            Number(features.Count);
            Number(features[0].Length);
            Number(BitConverter.DoubleToInt64Bits(penalty));
            foreach (double[] row in features)
                foreach (double value in row) Number(BitConverter.DoubleToInt64Bits(value));
            foreach (double value in targets) Number(BitConverter.DoubleToInt64Bits(value));
            return "ridge-solver/v1:" + Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, offset)));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private sealed class Scope(IRidgeFitCache? previous) : IDisposable
    {
        public void Dispose() => Active.Value = previous;
    }
}
