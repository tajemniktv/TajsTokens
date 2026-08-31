namespace TajsTokens.Core.Models;

/// <summary>
/// A complete JSONL record read from a source file. Payload is transient ingestion input and must
/// not be persisted wholesale by the telemetry repository.
/// </summary>
public sealed record RawSessionRecord(
    string FilePath,
    long StartByteOffset,
    long EndByteOffset,
    string Payload);
