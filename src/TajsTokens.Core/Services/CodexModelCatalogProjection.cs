using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public static class CodexModelCatalogProjection
{
    public static CodexModelCatalogView Build(CodexModelCatalogHistory history, IReadOnlyList<CodexLedgerEntry> selected)
    {
        var versions = history.Versions.OrderByDescending(x => x.LastCollectedAtUtc).ToArray();
        var latest = versions.FirstOrDefault();
        var metadata = versions.SelectMany(x => x.Definition.Models.Select(m => (Model: m, x.Definition.DefinitionId)))
            .GroupBy(x => x.Model.Model, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var observed = selected.Where(x => !string.IsNullOrWhiteSpace(x.Workload.Model)).GroupBy(x => x.Workload.Model!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        var current = latest?.Definition.Models.Select(x => x.Model).ToHashSet(StringComparer.Ordinal) ?? [];
        var choices = metadata.Keys.Concat(observed.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(model =>
        {
            metadata.TryGetValue(model, out var entry); observed.TryGetValue(model, out var rows);
            return new CodexModelChoice(model, entry.Model, entry.DefinitionId, current.Contains(model), rows is { Length: > 0 }, rows?.Length ?? 0,
                Array.AsReadOnly(rows?.Select(x => x.Workload.ReasoningEffort).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>()
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? []));
        }).ToArray();
        return new(history, Array.AsReadOnly(choices));
    }
}
