// Taj's Tokens | CodexSequenceComparison.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Infrastructure.Ingestion;

/// <summary>Research-only sequence relationships. Inputs are opaque content-free token fingerprints.</summary>
public static class CodexSequenceComparison
{

    public static Summary Compare(IReadOnlyList<Sequence> sequences)
    {
        // Inverted index avoids comparing every pair of unrelated, long rollouts. Repetition
        // within a file remains in its ordered sequence, but contributes only once to the index.
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var pairs = new HashSet<(int Left, int Right)>();
        for (int i = 0; i < sequences.Count; i++)
            foreach (string fingerprint in sequences[i].Fingerprints.Distinct(StringComparer.Ordinal))
            {
                if (!index.TryGetValue(fingerprint, out List<int>? files)) index[fingerprint] = files = [];
                foreach (int earlier in files) pairs.Add((earlier, i));
                files.Add(i);
            }
        int equal = 0, prefixes = 0, divergent = 0, overlaps = 0, crossOwner = 0;
        long shared = 0;
        foreach ((int left, int right) in pairs)
        {
            Sequence a = sequences[left];
            Sequence b = sequences[right];
            if (a.Owner is null || b.Owner is null || !StringComparer.OrdinalIgnoreCase.Equals(a.Owner, b.Owner)) crossOwner++;
            int common = 0;
            while (common < Math.Min(a.Fingerprints.Count, b.Fingerprints.Count) &&
                   a.Fingerprints[common] == b.Fingerprints[common]) common++;
            shared += common;
            if (common == a.Fingerprints.Count && common == b.Fingerprints.Count) equal++;
            else if (common == Math.Min(a.Fingerprints.Count, b.Fingerprints.Count)) prefixes++;
            else if (common > 0) divergent++;
            else overlaps++;
        }
        return new Summary(pairs.Count, equal, prefixes, divergent, overlaps, crossOwner, shared);
    }

    public sealed record Sequence(string? Owner, IReadOnlyList<string> Fingerprints);

    public sealed record Summary(
        int CandidatePairs,
        int EqualSequences,
        int StrictPrefixes,
        int DivergentPrefixes,
        int NonPrefixOverlap,
        int CrossOwnerPairs,
        long SharedPrefixOccurrences);
}