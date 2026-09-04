using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexCliHarnessTests
{
    [Fact]
    public void ParseLines_UsesOneExplicitArgumentPerLine()
    {
        var arguments = CodexCliArguments.ParseLines(" exec \r\n\r\n--json\n --model=gpt-test ");

        Assert.Equal(["exec", "--json", "--model=gpt-test"], arguments);
    }

    [Fact]
    public void BuildArguments_AppendsPromptAsOneFinalArgumentWhenRequested()
    {
        var request = new CodexCliHarnessRequest(
            "custom-codex",
            ["exec", "--json"],
            null,
            "prompt with spaces & symbols",
            CodexCliPromptMode.LastArgument,
            TimeSpan.FromMinutes(1));

        var arguments = CodexCliHarnessService.BuildArguments(request);

        Assert.Equal(["exec", "--json", "prompt with spaces & symbols"], arguments);
    }

    [Fact]
    public void BuildArguments_DoesNotAddPromptForStandardInputOrNone()
    {
        var standardInput = new CodexCliHarnessRequest(
            "custom-codex", ["exec"], null, "prompt", CodexCliPromptMode.StandardInput, TimeSpan.FromMinutes(1));
        var none = standardInput with { PromptMode = CodexCliPromptMode.None };

        Assert.Equal(["exec"], CodexCliHarnessService.BuildArguments(standardInput));
        Assert.Equal(["exec"], CodexCliHarnessService.BuildArguments(none));
    }
}
