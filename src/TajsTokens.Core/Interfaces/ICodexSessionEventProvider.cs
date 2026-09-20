// Taj's Tokens | ICodexSessionEventProvider.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Interfaces;

public interface ICodexSessionEventProvider
{
    IAsyncEnumerable<RawSessionRecord> ReadNewJsonLinesAsync(
        string filePath,
        long fromOffset,
        CancellationToken cancellationToken);
}