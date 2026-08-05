using Anchor;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AnchorTray.Tests;

[TestClass]
public sealed class PolicyEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void DryRunReturnsToDisarmedBatteryStateUntilAcReturns()
    {
        var policy = CreatePendingPolicy();
        var deadline = policy.ShutdownDeadline!.Value;

        Assert.AreEqual(PolicyEffect.ShutdownNow, policy.Tick(deadline));
        Assert.AreEqual(PolicyState.ShutdownIssued, policy.State);

        Assert.AreEqual(
            PolicyEffect.None,
            policy.CompleteShutdownAttempt(ShutdownAttemptResult.SuppressedDryRun, deadline));
        Assert.AreEqual(PolicyState.OnBattery, policy.State);
        Assert.IsNull(policy.ShutdownDeadline);

        // Remaining low and on battery must not immediately start another dry run.
        Assert.AreEqual(PolicyEffect.None, policy.Update(10, 0, available: true, deadline.AddSeconds(1)));
        Assert.AreEqual(PolicyState.OnBattery, policy.State);

        // Confirmed AC restoration re-arms the policy for a later outage.
        Assert.AreEqual(PolicyEffect.EnteredGrid, policy.Update(10, 100, available: true, deadline.AddSeconds(2)));
        Assert.AreEqual(PolicyState.OnGrid, policy.State);
        Assert.AreEqual(PolicyEffect.EnteredBattery, policy.Update(10, 0, available: true, deadline.AddSeconds(3)));
    }

    [TestMethod]
    public void FailedShutdownReturnsToPendingAndRetriesAfterThirtySeconds()
    {
        var policy = CreatePendingPolicy();
        var deadline = policy.ShutdownDeadline!.Value;
        Assert.AreEqual(PolicyEffect.ShutdownNow, policy.Tick(deadline));

        Assert.AreEqual(
            PolicyEffect.ShutdownFailed,
            policy.CompleteShutdownAttempt(ShutdownAttemptResult.Failed, deadline));
        Assert.AreEqual(PolicyState.Pending, policy.State);
        Assert.AreEqual(deadline.AddSeconds(30), policy.ShutdownDeadline);

        Assert.AreEqual(PolicyEffect.None, policy.Tick(deadline.AddSeconds(29)));
        Assert.AreEqual(PolicyEffect.ShutdownNow, policy.Tick(deadline.AddSeconds(30)));
        Assert.AreEqual(PolicyState.ShutdownIssued, policy.State);
    }

    [TestMethod]
    public void AcRestorationCancelsRetryAfterFailedShutdown()
    {
        var policy = CreatePendingPolicy();
        var deadline = policy.ShutdownDeadline!.Value;
        Assert.AreEqual(PolicyEffect.ShutdownNow, policy.Tick(deadline));
        Assert.AreEqual(
            PolicyEffect.ShutdownFailed,
            policy.CompleteShutdownAttempt(ShutdownAttemptResult.Failed, deadline));

        Assert.AreEqual(
            PolicyEffect.CountdownCancelledAcRestored,
            policy.Update(10, 100, available: true, deadline.AddSeconds(1)));
        Assert.AreEqual(PolicyState.OnGrid, policy.State);
        Assert.IsNull(policy.ShutdownDeadline);
    }

    [TestMethod]
    public void AcceptedShutdownRemainsTerminal()
    {
        var policy = CreatePendingPolicy();
        var deadline = policy.ShutdownDeadline!.Value;
        Assert.AreEqual(PolicyEffect.ShutdownNow, policy.Tick(deadline));

        Assert.AreEqual(
            PolicyEffect.None,
            policy.CompleteShutdownAttempt(ShutdownAttemptResult.Accepted, deadline));
        Assert.AreEqual(PolicyState.ShutdownIssued, policy.State);
        Assert.IsNull(policy.ShutdownDeadline);
    }

    private static PolicyEngine CreatePendingPolicy()
    {
        var policy = new PolicyEngine(
            configured: true, floorPercent: 15, debounceSamples: 2, countdownSeconds: 60);

        Assert.AreEqual(PolicyEffect.BecameAvailable, policy.Update(-1, -1, available: true, Start));
        Assert.AreEqual(PolicyEffect.EnteredGrid, policy.Update(50, 100, available: true, Start));
        Assert.AreEqual(PolicyEffect.EnteredBattery, policy.Update(10, 0, available: true, Start.AddSeconds(1)));
        Assert.AreEqual(PolicyEffect.CountdownStarted, policy.Update(10, 0, available: true, Start.AddSeconds(2)));
        Assert.AreEqual(PolicyState.Pending, policy.State);
        return policy;
    }
}
