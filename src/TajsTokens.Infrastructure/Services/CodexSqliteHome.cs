using Tomlyn;
using Tomlyn.Model;

namespace TajsTokens.Infrastructure.Services;

/// <summary>Read-only global config discovery; never changes Codex configuration.</summary>
internal static class CodexSqliteHome
{
    // Corroborated against codex a51608398d: explicit sqlite_home precedes the environment.
    // Process/project overrides cannot be inferred from another process's local files.
    internal static string? Resolve(string codexHome, string? environmentHome)
    {
        try
        {
            var config = Path.Combine(codexHome, "config.toml");
            if (File.Exists(config))
            {
                if (new FileInfo(config).Length > 1024 * 1024) return null;
                var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(config));
                if (model is not null && model.TryGetValue("sqlite_home", out var value))
                    return value is string path && !string.IsNullOrWhiteSpace(path)
                        ? Path.GetFullPath(path, codexHome) : null;
            }
            if (string.IsNullOrWhiteSpace(environmentHome)) return codexHome;
            // A relative environment override depends on the native process cwd, not ours.
            return Path.IsPathFullyQualified(environmentHome.Trim()) ? Path.GetFullPath(environmentHome.Trim()) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or TomlException)
        { return null; }
    }
}
