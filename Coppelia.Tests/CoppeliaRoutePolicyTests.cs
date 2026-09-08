using System.Numerics;
using System.Runtime.CompilerServices;
using Coppelia.Models;

namespace Coppelia.Tests;

public sealed class CoppeliaRoutePolicyTests
{
    private static readonly DateTime Started = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void InitialRouteIsImmediatelyEligible()
    {
        var policy = ReadyPolicy(new Vector3(1, 2, 3));

        Assert.True(policy.CanStart(pathfindInProgress: false, pathRunning: false));
    }

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
    public void NewerSnapshotsDoNotRefreshBeforeTenSecondsOrWithinFiveYalms()
    {
        var policy = ReadyPolicy(Vector3.Zero);
        policy.MarkStartupAccepted(Started);
        policy.AcceptSnapshot(2, new Vector3(6, 0, 0));
        Assert.Equal(
            CoppeliaRouteActivity.Owned,
            policy.Observe(pathfindInProgress: false, pathRunning: true, Started.AddSeconds(1)));

        Assert.Equal(
            CoppeliaRouteInterruption.None,
            policy.RefreshStaleDestination(
                pathfindInProgress: false,
                pathRunning: true,
                Started.AddSeconds(10).AddTicks(-1)));

        policy.AcceptSnapshot(3, new Vector3(0, 3, 4));
        Assert.Equal(
            CoppeliaRouteInterruption.None,
            policy.RefreshStaleDestination(
                pathfindInProgress: false,
                pathRunning: true,
                Started + CoppeliaRoutePolicy.DestinationRefreshInterval));
        Assert.True(policy.OwnsRoute);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void OwnedPendingOrMovingRouteRefreshesAtThreshold(
        bool pathfindInProgress,
        bool pathRunning,
        bool shouldCancelPending)
    {
        var expectedInterruption = shouldCancelPending
            ? CoppeliaRouteInterruption.StopPathAndCancelPending
            : CoppeliaRouteInterruption.StopPath;
        var policy = ReadyPolicy(Vector3.Zero);
        policy.MarkStartupAccepted(Started);
        policy.AcceptSnapshot(2, new Vector3(6, 0, 0));
        Assert.Equal(
            CoppeliaRouteActivity.Owned,
            policy.Observe(
                pathfindInProgress,
                pathRunning,
                Started + CoppeliaRoutePolicy.DestinationRefreshInterval));

        Assert.Equal(
            expectedInterruption,
            policy.RefreshStaleDestination(
                pathfindInProgress,
                pathRunning,
                Started + CoppeliaRoutePolicy.DestinationRefreshInterval));
        Assert.False(policy.OwnsRoute);
        Assert.True(policy.HasDestination);
        Assert.Equal(2, policy.LatestSequence);
        Assert.Equal(new Vector3(6, 0, 0), policy.LatestDestination);
        Assert.False(policy.CanStart(pathfindInProgress, pathRunning));
        Assert.True(policy.CanStart(pathfindInProgress: false, pathRunning: false));
    }

    [Fact]
    public void RefreshIntervalResetsAfterReplacementRouteStarts()
    {
        var policy = ReadyPolicy(Vector3.Zero);
        policy.MarkStartupAccepted(Started);
        policy.AcceptSnapshot(2, new Vector3(6, 0, 0));
        policy.Observe(
            pathfindInProgress: false,
            pathRunning: true,
            Started + CoppeliaRoutePolicy.DestinationRefreshInterval);
        Assert.Equal(
            CoppeliaRouteInterruption.StopPath,
            policy.RefreshStaleDestination(
                pathfindInProgress: false,
                pathRunning: true,
                Started + CoppeliaRoutePolicy.DestinationRefreshInterval));

        var replacementStarted = Started + CoppeliaRoutePolicy.DestinationRefreshInterval;
        policy.MarkStartupAccepted(replacementStarted);
        policy.AcceptSnapshot(3, new Vector3(12, 0, 0));
        policy.Observe(pathfindInProgress: false, pathRunning: true, replacementStarted.AddSeconds(1));

        Assert.Equal(
            CoppeliaRouteInterruption.None,
            policy.RefreshStaleDestination(
                pathfindInProgress: false,
                pathRunning: true,
                replacementStarted.AddSeconds(10).AddTicks(-1)));
        Assert.Equal(
            CoppeliaRouteInterruption.StopPath,
            policy.RefreshStaleDestination(
                pathfindInProgress: false,
                pathRunning: true,
                replacementStarted + CoppeliaRoutePolicy.DestinationRefreshInterval));
    }

    [Fact]
    public void ExternalRouteRemainsUntouched()
    {
        var policy = ReadyPolicy(Vector3.Zero);
        policy.AcceptSnapshot(2, new Vector3(6, 0, 0));

        Assert.Equal(
            CoppeliaRouteActivity.Other,
            policy.Observe(pathfindInProgress: true, pathRunning: false, Started));
        Assert.Equal(
            CoppeliaRouteInterruption.None,
            policy.RefreshStaleDestination(
                pathfindInProgress: true,
                pathRunning: false,
                Started + CoppeliaRoutePolicy.DestinationRefreshInterval));
        Assert.False(policy.OwnsRoute);
        Assert.True(policy.HasDestination);
        Assert.False(policy.CanStart(pathfindInProgress: true, pathRunning: false));
        Assert.True(policy.CanStart(pathfindInProgress: false, pathRunning: false));
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
            CoppeliaRouteActivity.Owned,
            policy.Observe(pathfindInProgress: false, pathRunning: true, Started.AddMilliseconds(150)));
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
        Assert.DoesNotContain("State = LineOfSightRescueState;", source, StringComparison.Ordinal);
        var rescueUpdateStart = source.IndexOf("public string UpdateLineOfSightRescue(", StringComparison.Ordinal);
        var rescueClearStart = source.IndexOf("public void ClearLineOfSightRescue()", StringComparison.Ordinal);
        var pairedOwnerCheck = source.IndexOf("if (routePolicy.OwnsRoute)", rescueUpdateStart, StringComparison.Ordinal);
        var rescueDestinationUpdate = source.IndexOf("var destinationChanged =", rescueUpdateStart, StringComparison.Ordinal);
        Assert.True(rescueUpdateStart >= 0);
        Assert.True(rescueClearStart > rescueUpdateStart);
        Assert.True(pairedOwnerCheck > rescueUpdateStart);
        Assert.True(rescueDestinationUpdate > pairedOwnerCheck);
        Assert.DoesNotContain("ReleaseOwnedRoute()", source[rescueUpdateStart..rescueClearStart], StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("var lineOfSightRescueActive =", StringComparison.Ordinal) <
            source.IndexOf("var routeActivity = routePolicy.Observe", StringComparison.Ordinal));
        Assert.Contains("if (lineOfSightRescueActive && !routePolicy.OwnsRoute)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lastMoveDestination", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingPathfindCancellationUsesGuardedOwnedInterruptions()
    {
        var source = ReadTravelServiceSource();
        var pauseStart = source.IndexOf("private void PauseOwnedRoute()", StringComparison.Ordinal);
        var releaseStart = source.IndexOf("private void ReleaseOwnedRoute()", StringComparison.Ordinal);
        var interruptStart = source.IndexOf("private void InterruptOwnedRoute(", StringComparison.Ordinal);
        var rescueStart = source.IndexOf("private void StopLineOfSightRescueRoute()", StringComparison.Ordinal);
        var stopStart = source.IndexOf("private void InvokePathStop()", StringComparison.Ordinal);

        Assert.True(pauseStart >= 0);
        Assert.True(releaseStart > pauseStart);
        Assert.True(interruptStart > releaseStart);
        Assert.True(rescueStart > interruptStart);
        Assert.True(stopStart > rescueStart);
        Assert.DoesNotContain("cancelAll", source[pauseStart..releaseStart], StringComparison.Ordinal);
        Assert.Contains("InvokePathStop", source[pauseStart..releaseStart], StringComparison.Ordinal);
        Assert.Contains("InterruptOwnedRoute(interruption)", source[releaseStart..interruptStart], StringComparison.Ordinal);
        Assert.Contains("cancelAll.InvokeAction()", source[interruptStart..rescueStart], StringComparison.Ordinal);
        Assert.Contains("InterruptOwnedRoute(refreshInterruption)", source, StringComparison.Ordinal);
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
