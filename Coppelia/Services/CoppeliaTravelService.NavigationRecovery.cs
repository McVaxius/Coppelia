using Coppelia.Models;

namespace Coppelia.Services;

internal sealed partial class CoppeliaTravelService
{
    private long navigationRecoveryGeneration;
    private bool navigationRecoveryHeld;
    private bool navigationRecoveryRetired;
    public bool NavigationRecoveryHeld => navigationRecoveryHeld || navigationRecoveryRetired;

    // Called only after the QST command endpoint verifies assignment ownership.
    public CoppeliaQstCommandResponse HoldForNavigationRecovery(long generation)
    {
        if (generation <= 0 || generation < navigationRecoveryGeneration ||
            generation == navigationRecoveryGeneration && !navigationRecoveryHeld)
            return new(false, "Stale navigation recovery generation.");
        if (navigationRecoveryHeld)
            return new(generation == navigationRecoveryGeneration, "Navigation recovery already holds this assignment.");
        navigationRecoveryGeneration = generation;
        navigationRecoveryHeld = true;
        // Evacuation must never wait for (or enqueue) ordinary landing cleanup.
        StopRidePath();
        ride = null;
        rideMode = null;
        rideOwnsMount = false;
        boardingConfirmed = false;
        Release();
        State = "Navigation recovery: navigation held";
        try
        {
            pathStop.InvokeAction();
            cancelAll.InvokeAction();
            return new(true, "Assignment navigation and queued pathfinding stopped.");
        }
        catch (Exception)
        {
            return new(false, "Navigation recovery could not stop vnav. Update or enable vnavmesh.");
        }
    }

    public CoppeliaQstCommandResponse FinishNavigationRecovery(long generation)
    {
        if (generation <= 0 || generation != navigationRecoveryGeneration)
            return new(false, "Stale navigation recovery generation.");
        // Keep navigation quiescent until assignment release. A delayed ordinary
        // TravelUpdate from before recovery must not move either client again.
        navigationRecoveryHeld = false;
        navigationRecoveryRetired = true;
        State = "Navigation recovery complete; waiting for fresh assignment travel";
        return new(true, State);
    }

    public void ForgetNavigationRecovery()
    {
        navigationRecoveryHeld = false;
        navigationRecoveryRetired = false;
        navigationRecoveryGeneration = 0;
    }
}
