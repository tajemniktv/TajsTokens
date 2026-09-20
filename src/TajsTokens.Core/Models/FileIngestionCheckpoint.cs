// Taj's Tokens | FileIngestionCheckpoint.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public sealed record FileIngestionCheckpoint(
    string FilePath,
    long LastByteOffset,
    DateTimeOffset UpdatedAtUtc,
    string? LastSessionId,
    string ParserVersion,
    string? SourceIdentity)
{
    public string? ConsumedPrefixSha256 { get; init; }
}