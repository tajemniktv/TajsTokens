using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TajsTokens.Core.Models;

public enum CodexModelCatalogReadState { Available, Absent, Invalid, Unavailable }
public sealed record CodexModelMetadata(string Model, string? DisplayName, IReadOnlyList<string> Aliases,
    IReadOnlyList<string> ReasoningEfforts, string? DefaultEffort, IReadOnlyList<string> InputModalities,
    IReadOnlyList<string> ServiceTiers, long? ContextWindow, long? MaxContextWindow, bool? ListedForApi,
    string? Visibility, DateTimeOffset? ReportedRetirementAtUtc, string? SuccessorModel, IReadOnlyList<string> Capabilities);

/// <summary>Content-addressed, allowlisted metadata; names and capabilities do not verify account entitlement.</summary>
public sealed class CodexModelCatalogDefinition
{
    public const string Version = "codex-model-catalog/v1";
    public const int MaximumModels = 512;
    [JsonConstructor]
    public CodexModelCatalogDefinition(string contract, string source, string? clientVersion, IReadOnlyList<CodexModelMetadata> models)
    {
        if (contract != Version || source != "codex-home/models_cache.json" || !Optional(clientVersion, 128) || models is null || models.Count > MaximumModels ||
            models.Any(x => x is null || !Label(x.Model, 256) || !Optional(x.DisplayName, 256) || !Optional(x.DefaultEffort, 128) ||
                !Optional(x.Visibility, 128) || !Optional(x.SuccessorModel, 256) || x.ContextWindow < 0 || x.MaxContextWindow < 0) ||
            models.Select(x => x.Model).Distinct(StringComparer.Ordinal).Count() != models.Count)
            throw new ArgumentException("Unsupported model catalog definition.");
        Contract = contract; Source = source; ClientVersion = clientVersion;
        Models = Array.AsReadOnly(models.Select(x => x with { Aliases = Labels(x.Aliases), ReasoningEfforts = Labels(x.ReasoningEfforts),
            InputModalities = Labels(x.InputModalities), ServiceTiers = Labels(x.ServiceTiers), Capabilities = Labels(x.Capabilities),
            ReportedRetirementAtUtc = x.ReportedRetirementAtUtc?.ToUniversalTime() }).OrderBy(x => x.Model, StringComparer.Ordinal).ToArray());
        DefinitionId = "codex-models:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Contract, Source, ClientVersion, Models }))));
    }
    public string Contract { get; }
    public string DefinitionId { get; }
    public string Source { get; }
    public string? ClientVersion { get; }
    public IReadOnlyList<CodexModelMetadata> Models { get; }
    private static IReadOnlyList<string> Labels(IReadOnlyList<string> labels)
    {
        if (labels is null || labels.Count > 32 || labels.Any(x => !Label(x, 256))) throw new ArgumentException("Invalid metadata labels.");
        return Array.AsReadOnly(labels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }
    private static bool Optional(string? label, int bound) => label is null || Label(label, bound);
    private static bool Label(string? label, int bound) => !string.IsNullOrWhiteSpace(label) && label.Length <= bound;
}

public sealed record CodexModelCatalogRead(CodexModelCatalogReadState State, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    DateTimeOffset? SourceFetchedAtUtc, CodexModelCatalogDefinition? Definition, string? Issue);
public sealed record CodexSavedModelCatalog(CodexModelCatalogDefinition Definition, DateTimeOffset FirstCollectedAtUtc,
    DateTimeOffset LastCollectedAtUtc, DateTimeOffset? FirstSourceFetchedAtUtc, DateTimeOffset? LastSourceFetchedAtUtc);
public sealed record CodexModelCatalogHistory(CodexModelCatalogRead? LastRead, IReadOnlyList<CodexSavedModelCatalog> Versions, bool IsPartial, int InvalidVersions)
{
    public static CodexModelCatalogHistory Empty { get; } = new(null, [], false, 0);
}
public sealed record CodexModelChoice(string Model, CodexModelMetadata? Metadata, string? DefinitionId,
    bool ListedInRetainedCatalog, bool ObservedInSelection, int SelectedObservations, IReadOnlyList<string> ObservedEfforts)
{
    public string AccountAccess => "Unverified";
    public string Description => $"{(ListedInRetainedCatalog ? "Cached catalog" : Metadata is not null ? "Historical catalog" : "No catalog entry")}" +
        (ObservedInSelection ? " · observed locally" : "") + " · account access unverified";
}
public sealed record CodexModelCatalogView(CodexModelCatalogHistory History, IReadOnlyList<CodexModelChoice> Choices)
{
    public static CodexModelCatalogView Empty { get; } = new(CodexModelCatalogHistory.Empty, []);
}
