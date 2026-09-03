using System.Globalization;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Services;

/// <summary>
/// Reconciles source-qualified Codex state observations into a presentation while retaining every
/// alternative and marking disagreements. This policy is deliberately separate from SQLite source
/// acquisition so precedence is explicit and reviewable at the read-model boundary.
/// </summary>
public static class CodexThreadReadModelPolicy
{
    public static CodexThreadStateReadModel Reconcile(
        IReadOnlyList<CodexThreadStateSourceObservation> observations)
    {
        var projectValues = observations
            .Where(observation => observation.Project is not null)
            .Select(observation => observation.Project!)
            .ToArray();
        var sectionValues = observations
            .Where(observation => observation.Section is not null)
            .Select(observation => observation.Section!)
            .ToArray();
        var toolValues = observations
            .Where(observation => observation.DynamicToolsCapabilityAvailable == true)
            .Select(observation => observation.DynamicTools)
            .ToArray();
        var edgeValues = observations
            .Where(observation => observation.SpawnEdgesCapabilityAvailable == true)
            .Select(observation => observation.SpawnEdges)
            .ToArray();

        var preferredProject = projectValues.FirstOrDefault();
        var projectRootsValues = projectValues.Select(project => project.OrderedRoots).ToArray();
        var preferredSection = sectionValues.FirstOrDefault();
        var preferredTools = toolValues.FirstOrDefault() ?? Array.Empty<CodexThreadDynamicTool>();
        var preferredEdges = edgeValues.FirstOrDefault() ?? Array.Empty<CodexThreadSpawnEdge>();
        var projectConflict = HasConflict(projectValues, ProjectFingerprint);
        var projectRootsConflict = HasConflict(projectRootsValues, RootsFingerprint);
        var sectionConflict = HasConflict(sectionValues, SectionFingerprint);
        var toolsConflict = HasConflict(toolValues, ToolsFingerprint);
        var edgesConflict = HasConflict(edgeValues, EdgesFingerprint);

        var rationale = string.Format(
            CultureInfo.InvariantCulture,
            "Optional state presentation uses the first readable value in explicit source order " +
            "(generation, write time, description, then path); {0} source observation(s) remain " +
            "source-qualified. Alternatives retained: project {1}, project roots {2}, section {3}, " +
            "dynamic tools {4}, spawn edges {5}. Conflicts: project {6}, project roots {7}, section {8}, " +
            "dynamic tools {9}, spawn edges {10}.",
            observations.Count,
            Math.Max(0, projectValues.Length - 1),
            Math.Max(0, projectRootsValues.Length - 1),
            Math.Max(0, sectionValues.Length - 1),
            Math.Max(0, toolValues.Length - 1),
            Math.Max(0, edgeValues.Length - 1),
            projectConflict ? "yes" : "no",
            projectRootsConflict ? "yes" : "no",
            sectionConflict ? "yes" : "no",
            toolsConflict ? "yes" : "no",
            edgesConflict ? "yes" : "no");

        return new CodexThreadStateReadModel(
            preferredProject,
            projectValues.Skip(1).ToArray(),
            projectConflict,
            projectRootsValues.Skip(1).ToArray(),
            projectRootsConflict,
            preferredSection,
            sectionValues.Skip(1).ToArray(),
            sectionConflict,
            preferredTools,
            toolValues.Skip(1).ToArray(),
            toolsConflict,
            preferredEdges,
            edgeValues.Skip(1).ToArray(),
            edgesConflict,
            rationale);
    }

    private static bool HasConflict<T>(IReadOnlyList<T> values, Func<T, string> fingerprint)
    {
        return values.Select(fingerprint).Distinct(StringComparer.Ordinal).Skip(1).Any();
    }

    private static string ProjectFingerprint(CodexThreadProject project) => string.Join(
        "\u001f",
        project.ProjectId,
        project.Name,
        project.Metadata,
        project.Position?.ToString(CultureInfo.InvariantCulture),
        project.CreatedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
        project.UpdatedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
        project.RootsCapabilityAvailable,
        string.Join("\u001e", project.OrderedRoots));

    private static string SectionFingerprint(CodexThreadSection section) => string.Join(
        "\u001f", section.SectionId, section.Name, section.Appearance);

    private static string RootsFingerprint(IReadOnlyList<string> roots) => string.Join("\u001e", roots);

    private static string ToolsFingerprint(IReadOnlyList<CodexThreadDynamicTool> tools) => string.Join(
        "\u001d",
        tools.Select(tool => string.Join(
            "\u001f",
            tool.Position,
            tool.Name,
            tool.Description,
            tool.InputSchema,
            tool.DeferLoading,
            tool.Namespace)));

    private static string EdgesFingerprint(IReadOnlyList<CodexThreadSpawnEdge> edges) => string.Join(
        "\u001d",
        edges.Select(edge => string.Join(
            "\u001f",
            edge.ParentThreadId,
            edge.ChildThreadId,
            edge.Status,
            edge.Depth)));
}
