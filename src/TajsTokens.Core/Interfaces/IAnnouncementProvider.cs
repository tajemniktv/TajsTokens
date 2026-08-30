using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface IAnnouncementProvider
{
    Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(CancellationToken cancellationToken);
}
