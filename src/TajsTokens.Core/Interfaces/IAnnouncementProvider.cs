// Taj's Tokens | IAnnouncementProvider.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Interfaces;

public interface IAnnouncementProvider
{
    Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(CancellationToken cancellationToken);
}