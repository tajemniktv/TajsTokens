using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexThreadPresentationTests
{
    [Fact]
    public void Present_UserContentArrayPreservesNonTextPartsAndSource()
    {
        var item = Item("userMessage", "{\"type\":\"userMessage\",\"content\":[{\"type\":\"text\",\"text\":\"hello\"},{\"type\":\"image\"}]}");

        var presentation = CodexThreadItemPresenter.Present(item);

        Assert.Equal(CodexThreadItemPresentationKind.Message, presentation.Kind);
        Assert.Equal("You", presentation.Heading);
        Assert.Equal($"hello{Environment.NewLine}[image]", presentation.Body);
        Assert.Same(item, presentation.Source);
        Assert.True(presentation.IsExpandedByDefault);
    }

    [Fact]
    public void Present_ReasoningUsesSummaryAndRemainsCollapsed()
    {
        var item = Item("reasoning", "{\"type\":\"reasoning\",\"summary\":[\"first\",\"second\"],\"content\":[\"raw\"]}");

        var presentation = CodexThreadItemPresenter.Present(item);

        Assert.Equal(CodexThreadItemPresentationKind.Reasoning, presentation.Kind);
        Assert.Equal($"first{Environment.NewLine}second", presentation.Body);
        Assert.Contains(presentation.Facts, fact => fact.Label == "Summary sections" && fact.Value == "2");
        Assert.False(presentation.IsExpandedByDefault);
    }

    [Fact]
    public void Present_CommandAndCollaborationExposeFactsAndLinkedThread()
    {
        var command = CodexThreadItemPresenter.Present(Item(
            "commandExecution",
            "{\"type\":\"commandExecution\",\"command\":\"dotnet test\",\"aggregatedOutput\":\"passed\",\"cwd\":\"C:/repo\",\"exitCode\":0,\"durationMs\":12}"));
        var collaboration = CodexThreadItemPresenter.Present(Item(
            "collabAgentToolCall",
            "{\"type\":\"collabAgentToolCall\",\"agentThreadId\":\"child-1\",\"prompt\":\"Inspect tests\"}"));

        Assert.Equal(CodexThreadItemPresentationKind.CommandExecution, command.Kind);
        Assert.Contains("dotnet test", command.Body);
        Assert.Contains(command.Facts, fact => fact.Label == "Exit code" && fact.Value == "0");
        Assert.Equal("child-1", collaboration.LinkedThreadId);
        Assert.Equal("Inspect tests", collaboration.Body);
    }

    [Fact]
    public void Present_AcceptsSnakeCaseNativeTypeTags()
    {
        var presentation = CodexThreadItemPresenter.Present(Item(
            "command_execution",
            "{\"type\":\"command_execution\",\"command\":\"echo ok\"}"));

        Assert.Equal(CodexThreadItemPresentationKind.CommandExecution, presentation.Kind);
    }

    [Theory]
    [InlineData("fileChange", "{\"type\":\"fileChange\",\"changes\":[{\"path\":\"README.md\"}]}", CodexThreadItemPresentationKind.FileChange)]
    [InlineData("mcpToolCall", "{\"type\":\"mcpToolCall\",\"server\":\"local\",\"tool\":\"search\",\"arguments\":{\"q\":\"x\"},\"result\":{\"ok\":true}}", CodexThreadItemPresentationKind.ToolCall)]
    [InlineData("dynamicToolCall", "{\"type\":\"dynamicToolCall\",\"namespace\":\"fs\",\"tool\":\"list\"}", CodexThreadItemPresentationKind.ToolCall)]
    [InlineData("functionCallOutput", "{\"type\":\"functionCallOutput\",\"name\":\"lookup\",\"output\":\"done\"}", CodexThreadItemPresentationKind.ToolCall)]
    [InlineData("plan", "{\"type\":\"plan\",\"text\":\"Do the work\"}", CodexThreadItemPresentationKind.Plan)]
    [InlineData("webSearch", "{\"type\":\"webSearch\"}", CodexThreadItemPresentationKind.SystemEvent)]
    [InlineData("contextCompaction", "{\"type\":\"contextCompaction\"}", CodexThreadItemPresentationKind.SystemEvent)]
    public void Present_SupportedNativeTypesProduceTolerantCards(
        string type,
        string json,
        CodexThreadItemPresentationKind expectedKind)
    {
        var item = Item(type, json);
        var presentation = CodexThreadItemPresenter.Present(item);

        Assert.Equal(expectedKind, presentation.Kind);
        Assert.Same(item, presentation.Source);
    }

    [Fact]
    public void Present_MalformedAndUnknownItemsRemainInspectable()
    {
        var malformed = Item("agentMessage", "{not-json");
        var unknown = Item("futureType", "{\"type\":\"futureType\",\"newField\":true}");

        var malformedPresentation = CodexThreadItemPresenter.Present(malformed);
        var unknownPresentation = CodexThreadItemPresenter.Present(unknown);

        Assert.Equal(CodexThreadItemPresentationKind.Unknown, malformedPresentation.Kind);
        Assert.Contains("malformed JSON", malformedPresentation.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Same(malformed, malformedPresentation.Source);
        Assert.Equal(CodexThreadItemPresentationKind.Unknown, unknownPresentation.Kind);
        Assert.Contains("futureType", unknownPresentation.Body);
        Assert.Same(unknown, unknownPresentation.Source);
    }

    private static CodexThreadItem Item(string type, string json) =>
        new("turn-1", "item-1", 1, null, type, json, null)
        {
            SourcePath = "history.sqlite",
            SourceDescription = "Test history"
        };
}
