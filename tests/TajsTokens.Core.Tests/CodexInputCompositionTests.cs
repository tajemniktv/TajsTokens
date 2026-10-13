// Taj's Tokens | CodexInputCompositionTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexInputCompositionTests
{
    [Fact]
    public void UsesOnlyRecordedInputComponents()
    {
        var totals = new CodexNativeTokenTotals(10, 80, 10, 900, 0, 1000);
        CodexInputComposition composition = Assert.IsType<CodexInputComposition>(CodexInputComposition.From(totals));
        Assert.Equal(80, composition.CacheReadPercent);
        Assert.Equal(10, composition.CacheWritePercent);
    }

    [Fact]
    public void EmptyOrInvalidInputHasNoComposition()
    {
        Assert.Null(CodexInputComposition.From(null));
        Assert.Null(CodexInputComposition.From(new(0, 0, 0, 10, 0, 10)));
        Assert.Null(CodexInputComposition.From(new(-1, 10, 0, 0, 0, 9)));
    }

    [Fact]
    public void LargeCountersDoNotOverflow()
    {
        CodexInputComposition composition = Assert.IsType<CodexInputComposition>(
            CodexInputComposition.From(new(long.MaxValue, long.MaxValue, 0, 0, 0, 0)));
        Assert.Equal(50, composition.CacheReadPercent);
    }
}
