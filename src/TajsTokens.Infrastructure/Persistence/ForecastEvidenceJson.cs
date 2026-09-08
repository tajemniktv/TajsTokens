using System.Text.Json;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

/// <summary>Only the typed, rebuildable forecast diagnostics payload; never raw source JSON.</summary>
internal static class ForecastEvidenceJson
{
    public static string Serialize(ForecastEvidence evidence) => JsonSerializer.Serialize(evidence);

    public static ForecastEvidence? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ForecastEvidence>(json); }
        catch (JsonException) { return null; } // Old/corrupt derived diagnostics do not replace current evidence.
    }
}
