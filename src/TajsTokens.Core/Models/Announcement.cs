namespace TajsTokens.Core.Models;

public sealed record Announcement(
    string AnnouncementId,
    DateTimeOffset PublishedAtUtc,
    string Source,
    string Title,
    string Body,
    Uri? Link);
