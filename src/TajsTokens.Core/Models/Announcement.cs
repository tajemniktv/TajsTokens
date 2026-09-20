// Taj's Tokens | Announcement.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public sealed record Announcement(
    string AnnouncementId,
    DateTimeOffset PublishedAtUtc,
    string Source,
    string Title,
    string Body,
    Uri? Link);