namespace TajsTokens.Core.Models;

/// <summary>
/// Stable, content-free repository identity observed by telemetry providers. Remote URLs are reduced
/// to credential-free identity metadata before they can reach durable persistence.
/// </summary>
public sealed record RepositoryIdentity
{
    public RepositoryIdentity(
        string repositoryId,
        string name,
        string? rootPath,
        string? remoteUrl,
        DateTimeOffset firstSeenAtUtc,
        DateTimeOffset lastSeenAtUtc)
    {
        RepositoryId = repositoryId;
        Name = name;
        RootPath = rootPath;
        RemoteUrl = SanitizeRemoteUrl(remoteUrl);
        FirstSeenAtUtc = firstSeenAtUtc;
        LastSeenAtUtc = lastSeenAtUtc;
    }

    public string RepositoryId { get; }
    public string Name { get; }
    public string? RootPath { get; }
    public string? RemoteUrl { get; }
    public DateTimeOffset FirstSeenAtUtc { get; }
    public DateTimeOffset LastSeenAtUtc { get; }

    internal static string? SanitizeRemoteUrl(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var trimmed = remoteUrl.Trim();

        // Git accepts SCP-style remotes such as user@host:path. Handle these before generic URI
        // parsing because a credential-like prefix such as oauth2:token@host:path can otherwise be
        // mistaken for a custom URI scheme. The account/token prefix is irrelevant to repository
        // identity, and queries/fragments are discarded as well.
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            var suffixStart = trimmed.IndexOfAny(['?', '#']);
            if (suffixStart >= 0)
            {
                trimmed = trimmed[..suffixStart];
            }

            var at = trimmed.IndexOf('@');
            var pathSeparator = at >= 0 ? trimmed.IndexOf(':', at + 1) : -1;
            if (at >= 0 && pathSeparator > at)
            {
                return trimmed[(at + 1)..];
            }
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Scheme))
        {
            var sanitized = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };
            return sanitized.Uri.AbsoluteUri;
        }

        return trimmed;
    }
}
