namespace TajsTokens.Core.Tests;

internal static class LegacyObservatoryFixture
{
    // These disposable fixtures recreate schemas that predate the additive projection cache.
    // Its current triggers must not remain attached while historical columns are removed.
    public const string RemoveProjectionCache = """
        DROP TRIGGER IF EXISTS native_token_projection_insert_v1;
        DROP TRIGGER IF EXISTS native_token_projection_update_v1;
        DROP TRIGGER IF EXISTS native_token_projection_delete_v1;
        DROP TABLE IF EXISTS codex_native_token_projection_revision;

        """;
}
