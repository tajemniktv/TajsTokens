// Taj's Tokens | ISessionIngestionCheckpointStore.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Interfaces;

public interface ISessionIngestionCheckpointStore
{
    Task<FileIngestionCheckpoint?> GetCheckpointAsync(string filePath, CancellationToken cancellationToken);
    Task SaveCheckpointAsync(FileIngestionCheckpoint checkpoint, CancellationToken cancellationToken);
}