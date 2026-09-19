using TajsTokens.Infrastructure.Ingestion;

namespace TajsTokens.Core.Tests;

public sealed class CodexSequenceComparisonTests
{
    [Theory]
    [InlineData("a,b", "a,b", 1, 0, 0, 0, 2)]
    [InlineData("a,b", "a,b,c", 0, 1, 0, 0, 2)]
    [InlineData("a,b,c", "a,b,d", 0, 0, 1, 0, 2)]
    [InlineData("a,b,c", "b,a,c", 0, 0, 0, 1, 0)]
    [InlineData("a,a,b", "a,b", 0, 0, 1, 0, 1)]
    public void ClassifiesOrderedOccurrencesWithoutCanonicalizing(string left, string right,
        int equal, int prefix, int divergent, int overlap, long shared)
    {
        var sequences = new[] { new CodexSequenceComparison.Sequence("owner", left.Split(',')),
            new CodexSequenceComparison.Sequence("other-owner", right.Split(',')) };
        var expected = new CodexSequenceComparison.Summary(1, equal, prefix, divergent, overlap, 1, shared);
        Assert.Equal(expected, CodexSequenceComparison.Compare(sequences));
        Assert.Equal(expected, CodexSequenceComparison.Compare(sequences.Reverse().ToArray()));
    }

    [Fact]
    public void EmptyAndUnrelatedStreamsAreNotCopyEvidence()
    {
        Assert.Equal(new CodexSequenceComparison.Summary(0, 0, 0, 0, 0, 0, 0),
            CodexSequenceComparison.Compare([new(null, []), new(null, []), new("a", ["one"]), new("b", ["two"])]));
    }

    [Fact]
    public void RepeatedFingerprintsDoNotMultiplyFilePairs()
    {
        var result = CodexSequenceComparison.Compare([new("a", ["x", "x"]), new("a", ["x", "x"]), new("a", ["x", "x"])]);
        Assert.Equal(3, result.CandidatePairs);
        Assert.Equal(3, result.EqualSequences);
        Assert.Equal(0, result.CrossOwnerPairs);
        Assert.Equal(6, result.SharedPrefixOccurrences);
    }
}
