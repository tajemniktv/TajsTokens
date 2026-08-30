namespace TajsTokens.Core.Models;

public sealed record FileIngestionCheckpoint(
    string FilePath,
    long LastByteOffset,
    DateTimeOffset UpdatedAtUtc,
    string? LastSessionId);
