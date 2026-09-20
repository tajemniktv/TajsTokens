// Taj's Tokens | IntelligenceArchitectureTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Tests;

public sealed class IntelligenceArchitectureTests
{
    [Fact]
    public void RuntimeCannotImportResearchAndProductEngineCannotReadSqlite()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (string path in Directory.EnumerateFiles(
                     Path.Combine(root.FullName, "src", "TajsTokens.Core", "Services"),
                     "*.cs",
                     SearchOption.AllDirectories))
            Assert.DoesNotContain("TajsTokens.Core.Research", File.ReadAllText(path));
        string engine = File.ReadAllText(
            Path.Combine(root.FullName, "src", "TajsTokens.Infrastructure", "Services", "CodexIntelligenceEngine.cs"));
        Assert.DoesNotContain("Sqlite", engine);
        Assert.DoesNotContain("SELECT ", engine);
        Assert.DoesNotContain("databasePath", engine);
    }
}