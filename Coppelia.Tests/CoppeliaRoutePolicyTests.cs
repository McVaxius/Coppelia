using System.Numerics;
using System.Runtime.CompilerServices;
using Coppelia.Models;

namespace Coppelia.Tests;

public sealed class CoppeliaRoutePolicyTests
{
    private static readonly DateTime Started = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ActiveOrPendingVnavActivityPreventsAnotherRouteStartup()
    {
        var policy = ReadyPolicy(new Vector3(1, 2, 3));

        Assert.False(policy.CanStart(pathfindInProgress: true, pathRunning: false));
        Assert.False(policy.CanStart(pathfindInProgress: false, pathRunning: true));

        policy.MarkStartupAccepted(Started);
        Assert.False(policy.CanStart(pathfindInProgress: false, pathRunning: false));
        Assert.Equal(CoppeliaRouteActivity.Owned, policy.Observe(pathfindInProgress: true, pathRunning: false, Started));
        Assert.True(policy.OwnsPendingPathfind);
    }

    [Fact]
    public void MovingSnapshotsDoNotReplaceAnOwnedRoute()
    {
        var policy = ReadyPolicy(new Vector3(1, 2, 3));
        policy.MarkStartupAccepted(Started);

        policy.AcceptSnapshot(2, new Vector3(4, 5, 6));

        Assert.True(policy.OwnsRoute);
        Assert.False(policy.CanStart(pathfindInProgress: false, pathRunning: false));
        Assert.Equal(new Vector3(4, 5, 6), policy.LatestDestination);
    }

    [Fact]
    public void CompletedRouteRestartsFromNewestAcceptedCoordinates()
    {
        var policy = ReadyPolicy(new Vector3(1, 2, 3));
        policy.MarkStartupAccepted(Started);
        policy.AcceptSnapshot(2, new Vector3(40, 50, 60));

        Assert.Equal(
            CoppeliaRouteActivity.Owned,
            policy.Observe(pathfindInProgress: true, pathRunning: false, Started.AddMilliseconds(100)));
        Assert.Equal(
            CoppeliaRouteActivity.Completed,
            policy.Observe(pathfindInProgress: false, pathRunning: false, Started.AddMilliseconds(200)));
        Assert.True(policy.CanStart(pathfindInProgress: false, pathRunning: false));
        Assert.Equal(2, policy.LatestSequence);
        Assert.Equal(new Vector3(40, 50, 60), policy.LatestDestination);
    }

    [Fact]
    public void AcceptedStartupThatNeverBecomesActiveIsRejectedUntilANewerSnapshot()
    {
        var policy = ReadyPolicy(new Vector3(1, 2, 3));
        policy.MarkStartupAccepted(Started);

        Assert.Equal(
            CoppeliaRouteActivity.Owned,
            policy.Observe(pathfindInProgress: false, pathRunning: false, Started.AddSeconds(1)));
        Assert.Equal(
            CoppeliaRouteActivity.Rejected,
            policy.Observe(pathfindInProgress: false, pathRunning: false, Started + CoppeliaRoutePolicy.StartupActivityTimeout));
        Assert.False(policy.CanStart(pathfindInProgress: false, pathRunning: false));

        policy.AcceptSnapshot(2, new Vector3(4, 5, 6));
        Assert.True(policy.CanStart(pathfindInProgress: false, pathRunning: false));
    }

    [Fact]
    public void NeverActiveRouteCanAdvanceToANewerSnapshotAcceptedWhilePending()
    {
        var policy = ReadyPolicy(new Vector3(1, 2, 3));
        policy.MarkStartupAccepted(Started);
        policy.AcceptSnapshot(2, new Vector3(4, 5, 6));

        Assert.Equal(
            CoppeliaRouteActivity.Rejected,
            policy.Observe(pathfindInProgress: false, pathRunning: false, Started + CoppeliaRoutePolicy.StartupActivityTimeout));
        Assert.False(policy.StartupRejected);
        Assert.True(policy.CanStart(pathfindInProgress: false, pathRunning: false));
        Assert.Equal(new Vector3(4, 5, 6), policy.LatestDestination);
    }

    [Fact]
    public void GenuinelyCompletedRouteDoesNotRestartTheSameSnapshot()
    {
        var policy = ReadyPolicy(new Vector3(1, 2, 3));
        policy.MarkStartupAccepted(Started);

        Assert.Equal(
            CoppeliaRouteActivity.Owned,
            policy.Observe(pathfindInProgress: false, pathRunning: true, Started.AddMilliseconds(100)));
        Assert.Equal(
            CoppeliaRouteActivity.Completed,
            policy.Observe(pathfindInProgress: false, pathRunning: false, Started.AddMilliseconds(200)));
        Assert.False(policy.CanStart(pathfindInProgress: false, pathRunning: false));
    }

    [Fact]
    public void RejectedStartupWaitsForANewerSnapshot()
    {
        var policy = ReadyPolicy(new Vector3(1, 2, 3));
        policy.MarkStartupRejected(pathfindInProgress: false, pathRunning: false);

        Assert.Equal(
            CoppeliaRouteActivity.Rejected,
            policy.Observe(pathfindInProgress: false, pathRunning: false, Started));
        Assert.False(policy.CanStart(pathfindInProgress: false, pathRunning: false));

        policy.AcceptSnapshot(2, new Vector3(4, 5, 6));
        Assert.True(policy.CanStart(pathfindInProgress: false, pathRunning: false));

        policy.MarkStartupRejected(pathfindInProgress: true, pathRunning: false);
        Assert.Equal(
            CoppeliaRouteActivity.Rejected,
            policy.Observe(pathfindInProgress: true, pathRunning: false, Started));
        Assert.Equal(
            CoppeliaRouteActivity.Rejected,
            policy.Observe(pathfindInProgress: false, pathRunning: false, Started.AddSeconds(10)));
        Assert.False(policy.CanStart(pathfindInProgress: false, pathRunning: false));

        policy.AcceptSnapshot(3, new Vector3(7, 8, 9));
        Assert.True(policy.CanStart(pathfindInProgress: false, pathRunning: false));
    }

    [Fact]
    public void ActionPauseStopsOnlyAnActivePath()
    {
        var policy = ReadyPolicy(new Vector3(1, 2, 3));
        policy.MarkStartupAccepted(Started);

        Assert.Equal(
            CoppeliaRouteActivity.Owned,
            policy.Observe(pathfindInProgress: false, pathRunning: true, Started.AddMilliseconds(100)));

        Assert.Equal(CoppeliaRouteInterruption.StopPath, policy.Pause(pathRunning: true));
        Assert.Equal(CoppeliaRouteInterruption.None, policy.Pause(pathRunning: false));
        Assert.True(policy.OwnsRoute);

        Assert.Equal(
            CoppeliaRouteActivity.Completed,
            policy.Observe(pathfindInProgress: false, pathRunning: false, Started.AddMilliseconds(200)));
        Assert.True(policy.CanStart(pathfindInProgress: false, pathRunning: false));
    }

    [Fact]
    public void PendingRouteThatCompletesDuringHoldIsStoppedBeforeResume()
    {
        var policy = ReadyPolicy(new Vector3(1, 2, 3));
        policy.MarkStartupAccepted(Started);

        Assert.Equal(
            CoppeliaRouteActivity.Owned,
            policy.Observe(pathfindInProgress: true, pathRunning: false, Started));
        Assert.Equal(CoppeliaRouteInterruption.None, policy.Pause(pathRunning: false));

        Assert.Equal(
            CoppeliaRouteActivity.Owned,
            policy.Observe(pathfindInProgress: false, pathRunning: true, Started.AddMilliseconds(100)));
        Assert.Equal(CoppeliaRouteInterruption.StopPath, policy.Pause(pathRunning: true));
    }

    [Fact]
    public void SuccessfulStartupSurvivesAnIdleHandoffObservation()
    {
        var policy = ReadyPolicy(new Vector3(1, 2, 3));
        policy.MarkStartupAccepted(Started);

        Assert.Equal(
            CoppeliaRouteActivity.Owned,
            policy.Observe(pathfindInProgress: false, pathRunning: false, Started.AddMilliseconds(100)));
        Assert.True(policy.OwnsRoute);
        Assert.Equal(
            CoppeliaRouteActivity.Owned,
            policy.Observe(pathfindInProgress: false, pathRunning: true, Started.AddMilliseconds(200)));
        Assert.True(policy.OwnsRoute);
    }

    [Fact]
    public void TerminalReleaseCancelsPendingPathfindingOnlyOnce()
    {
        var policy = ReadyPolicy(new Vector3(1, 2, 3));
        policy.MarkStartupAccepted(Started);

        Assert.Equal(
            CoppeliaRouteInterruption.StopPathAndCancelPending,
            policy.Release(pathfindInProgress: true, pathRunning: false));
        Assert.Equal(
            CoppeliaRouteInterruption.None,
            policy.Release(pathfindInProgress: true, pathRunning: false));
        Assert.False(policy.OwnsRoute);
        Assert.False(policy.HasDestination);
    }

    [Fact]
    public void TerminalReleaseDoesNotCancelLaterUnownedPathfinding()
    {
        var policy = ReadyPolicy(new Vector3(1, 2, 3));
        policy.MarkStartupAccepted(Started);
        Assert.Equal(
            CoppeliaRouteActivity.Owned,
            policy.Observe(pathfindInProgress: false, pathRunning: true, Started));
        Assert.False(policy.OwnsPendingPathfind);

        Assert.Equal(
            CoppeliaRouteInterruption.StopPath,
            policy.Release(pathfindInProgress: true, pathRunning: true));
    }

    [Fact]
    public void TravelAndLosRescueUseGuardedSimpleMoveStartups()
    {
        var source = ReadTravelServiceSource();

        Assert.Contains("vnavmesh.SimpleMove.PathfindInProgress", source, StringComparison.Ordinal);
        Assert.Contains("routePolicy.Observe(isPathfinding, isPathRunning, DateTime.UtcNow)", source, StringComparison.Ordinal);
        Assert.Contains("routePolicy.CanStart(isPathfinding, isPathRunning)", source, StringComparison.Ordinal);
        Assert.Contains("lineOfSightRescueRoutePolicy.CanStart(isPathfinding, isPathRunning)", source, StringComparison.Ordinal);
        Assert.Equal(2, Count(source, "moveCloseTo.InvokeFunc("));
        Assert.Contains("LOS blocked; waiting for current movement owner", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lastMoveDestination", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NavmeshReloadIsConfinedToTerminalRelease()
    {
        var source = ReadTravelServiceSource();
        var pauseStart = source.IndexOf("private void PauseOwnedRoute()", StringComparison.Ordinal);
        var releaseStart = source.IndexOf("private void ReleaseOwnedRoute()", StringComparison.Ordinal);
        var stopStart = source.IndexOf("private void InvokePathStop()", StringComparison.Ordinal);

        Assert.True(pauseStart >= 0);
        Assert.True(releaseStart > pauseStart);
        Assert.True(stopStart > releaseStart);
        Assert.DoesNotContain("cancelAll", source[pauseStart..releaseStart], StringComparison.Ordinal);
        Assert.Contains("InvokePathStop", source[pauseStart..releaseStart], StringComparison.Ordinal);
        Assert.Contains("cancelAll.InvokeAction()", source[releaseStart..stopStart], StringComparison.Ordinal);
        Assert.Equal(2, Count(source, "cancelAll.InvokeAction()"));
    }

    private static CoppeliaRoutePolicy ReadyPolicy(Vector3 destination)
    {
        var policy = new CoppeliaRoutePolicy();
        policy.AcceptSnapshot(1, destination);
        return policy;
    }

    private static string ReadTravelServiceSource([CallerFilePath] string testSourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testSourcePath)!,
            "..",
            "Coppelia",
            "Services",
            "CoppeliaTravelService.cs")));

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;
}
