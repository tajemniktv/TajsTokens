// Taj's Tokens | CodexRemoteFleetHostPolicyTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using TajsTokens.Core.Models;

namespace TajsTokens.Core.Tests;

public sealed class CodexRemoteFleetHostPolicyTests
{
    [Theory]
    [InlineData("server", true)]
    [InlineData("my.laptop_1", true)]
    [InlineData("-oProxyCommand=evil", false)]
    [InlineData("host;command", false)]
    [InlineData("user@host", false)]
    [InlineData("*.example", false)]
    [InlineData("", false)]
    public void AcceptsOnlySimpleSshConfigAliases(string host, bool expected)
    {
        Assert.Equal(expected, CodexRemoteFleetHostPolicy.IsValid(host));
    }
}
