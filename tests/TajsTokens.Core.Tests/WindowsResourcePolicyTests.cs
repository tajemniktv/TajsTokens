using TajsTokens.App.Services;

namespace TajsTokens.Core.Tests;

public sealed class WindowsResourcePolicyTests
{
    [Fact]
    public void FailedForegroundTransitionRetriesWithoutChangingWorkerVisibility()
    {
        var calls = new List<bool>();
        bool fail = true;
        using var policy = new WindowsResourcePolicy(enabled =>
        {
            calls.Add(enabled);
            return !enabled && fail ? 5 : 0;
        });
        policy.SetDashboardVisible(false);
        policy.SetDashboardVisible(true);
        Assert.False(policy.IsBackground);
        Assert.True(policy.AppliedEcoQoS);
        Assert.Equal(5, policy.LastError);
        Assert.Contains("pending", policy.Diagnostic);
        fail = false;
        policy.SetDashboardVisible(true);
        policy.SetDashboardVisible(true);
        Assert.Equal(new[] { true, false, false }, calls);
        Assert.False(policy.AppliedEcoQoS);
        Assert.Null(policy.LastError);
    }

    [Fact]
    public void FailedBackgroundTransitionDoesNotPoisonForegroundState()
    {
        var calls = new List<bool>();
        using var policy = new WindowsResourcePolicy(enabled =>
        {
            calls.Add(enabled);
            return enabled ? 50 : 0;
        });
        policy.SetDashboardVisible(true);
        policy.SetDashboardVisible(false);
        Assert.True(policy.IsBackground);
        Assert.False(policy.AppliedEcoQoS);
        policy.SetDashboardVisible(false);
        policy.SetDashboardVisible(true);
        Assert.False(policy.IsBackground);
        Assert.Equal(new[] { false, true, true }, calls);
        Assert.False(policy.AppliedEcoQoS);
        Assert.Null(policy.LastError);
    }

    [Fact]
    public void DisposeRestoresHintOnceAndLaterRequestsDoNothing()
    {
        var calls = new List<bool>();
        var policy = new WindowsResourcePolicy(enabled => { calls.Add(enabled); return 0; });
        policy.SetDashboardVisible(false);
        policy.Dispose();
        policy.Dispose();
        policy.SetDashboardVisible(false);
        Assert.Equal(new[] { true, false }, calls);
        Assert.False(policy.IsBackground);
    }
}
