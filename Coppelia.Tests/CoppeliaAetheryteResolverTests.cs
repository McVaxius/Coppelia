using System.Numerics;
using System.Runtime.CompilerServices;
using Coppelia.Models;

namespace Coppelia.Tests;

public sealed class CoppeliaAetheryteResolverTests
{
    [Fact]
    public void ExactMembershipPreservesTeleportListSubIndex()
    {
        var teleportList = new[]
        {
            new CoppeliaTeleportListEntry(4, 2, 100),
            new CoppeliaTeleportListEntry(4, 9, 200),
        };

        Assert.True(CoppeliaTeleportIntentPolicy.TryFindExact(teleportList, 4, 9, out var selected));
        Assert.Equal((byte)9, selected.SubIndex);
        Assert.Equal((uint)200, selected.GilCost);
        Assert.False(CoppeliaTeleportIntentPolicy.TryFindExact(teleportList, 4, 0, out _));
    }

    [Fact]
    public void LevelTraversalUsesLaterEmbeddedAndDirectRows()
    {
        var directLookups = new List<uint>();
        var directResult = CoppeliaAetherytePolicy.ResolveLevelPosition(
            new[]
            {
                new CoppeliaAetheryteLevelReference(10, Vector3.Zero),
                new CoppeliaAetheryteLevelReference(20, null),
                new CoppeliaAetheryteLevelReference(30, new Vector3(30, 3, 30)),
            },
            rowId =>
            {
                directLookups.Add(rowId);
                return rowId == 20 ? new Vector3(20, 2, 20) : null;
            });

        Assert.Equal(new Vector3(20, 2, 20), directResult);
        Assert.Equal(new uint[] { 20 }, directLookups);

        var laterEmbedded = CoppeliaAetherytePolicy.ResolveLevelPosition(
            new[]
            {
                new CoppeliaAetheryteLevelReference(40, null),
                new CoppeliaAetheryteLevelReference(50, new Vector3(50, 5, 50)),
            },
            _ => Vector3.Zero);
        Assert.Equal(new Vector3(50, 5, 50), laterEmbedded);
    }

    [Fact]
    public void ExactMapMarkerMatchLeavesOtherUnresolvedCandidatesUnassigned()
    {
        var selection = CoppeliaAetherytePolicy.Resolve(
            100,
            new Vector3(20, 0, 20),
            new[]
            {
                Candidate(10, position: Vector3.Zero, placeNameId: 110),
                Candidate(20, position: Vector3.Zero, placeNameId: 120),
            },
            avoidTamamizu: true,
            new CoppeliaAetheryteMapTransform(100, 0, 0),
            new[]
            {
                new CoppeliaAetheryteMapMarker(1034, 1034, 3, 999),
                new CoppeliaAetheryteMapMarker(1044, 1044, 3, 120),
            });

        Assert.NotNull(selection);
        Assert.Equal((uint)20, selection.Candidate.Id);
        Assert.Equal(new Vector3(20, 0, 20), selection.Candidate.Position);
    }

    [Fact]
    public void MapMarkerSequentialFallbackUsesMarkerAndTeleportListOrder()
    {
        var selection = CoppeliaAetherytePolicy.Resolve(
            100,
            new Vector3(20, 0, 20),
            new[]
            {
                Candidate(10, position: Vector3.Zero),
                Candidate(20, position: Vector3.Zero),
            },
            avoidTamamizu: true,
            new CoppeliaAetheryteMapTransform(100, 0, 0),
            new[]
            {
                new CoppeliaAetheryteMapMarker(1034, 1034, 3, 900),
                new CoppeliaAetheryteMapMarker(1044, 1044, 4, 901),
            });

        Assert.NotNull(selection);
        Assert.Equal((uint)20, selection.Candidate.Id);
        Assert.Equal(new Vector3(20, 0, 20), selection.Candidate.Position);
    }

    [Fact]
    public void MapLocationNameOverrideWinsEvenWithoutPosition()
    {
        var selection = CoppeliaAetherytePolicy.Resolve(
            100,
            new Vector3(5, 0, 5),
            new[]
            {
                Candidate(10, name: "First", position: new Vector3(5, 0, 5)),
                Candidate(20, name: "Override", position: Vector3.Zero),
            },
            avoidTamamizu: true,
            mapLocation: new CoppeliaMapLocationSelection("override", false, Vector3.Zero));

        Assert.NotNull(selection);
        Assert.Equal((uint)20, selection.Candidate.Id);
        Assert.Equal(double.MaxValue, selection.Distance);
        Assert.False(selection.UsedXyzComparison);
    }

    [Fact]
    public void RealXyzUsesFullDistanceForStoredPositions()
    {
        var candidates = new[]
        {
            Candidate(10, position: new Vector3(0, 100, 0), hasStoredPosition: true),
            Candidate(20, position: new Vector3(30, 0, 0), hasStoredPosition: true),
        };
        var mapLocation = new CoppeliaMapLocationSelection("", true, new Vector3(0, 100, 0));

        var selection = CoppeliaAetherytePolicy.Resolve(
            100,
            new Vector3(29, 0, 1),
            candidates,
            avoidTamamizu: true,
            mapLocation: mapLocation);

        Assert.NotNull(selection);
        Assert.Equal((uint)10, selection.Candidate.Id);
        Assert.True(selection.UsedXyzComparison);
        Assert.True(selection.WinnerUsedXyz);
        Assert.Equal(0, selection.Distance);
    }

    [Fact]
    public void LevelPositionUsesXzEvenWhenDestinationHasRealY()
    {
        var selection = CoppeliaAetherytePolicy.Resolve(
            100,
            new Vector3(1, 0, 1),
            new[]
            {
                Candidate(10, position: new Vector3(0, 999, 0), hasStoredPosition: false),
                Candidate(20, position: new Vector3(3, 0, 0), hasStoredPosition: true),
            },
            avoidTamamizu: true,
            mapLocation: new CoppeliaMapLocationSelection("", true, Vector3.Zero));

        Assert.NotNull(selection);
        Assert.Equal((uint)10, selection.Candidate.Id);
        Assert.True(selection.UsedXyzComparison);
        Assert.False(selection.WinnerUsedXyz);
        Assert.Equal(0, selection.Distance);
    }

    [Fact]
    public void TamamizuSettingFiltersOnlyOrdinaryResolution()
    {
        var candidates = new[]
        {
            Candidate(CoppeliaAetherytePolicy.TamamizuAetheryteId, name: "Tamamizu", position: new Vector3(1, 0, 1)),
            Candidate(106, name: "Onokoro", position: new Vector3(100, 0, 100)),
        };

        Assert.Equal(
            (uint)106,
            CoppeliaAetherytePolicy.Resolve(100, new Vector3(2, 0, 2), candidates, avoidTamamizu: true)!.Candidate.Id);
        Assert.Equal(
            CoppeliaAetherytePolicy.TamamizuAetheryteId,
            CoppeliaAetherytePolicy.Resolve(100, new Vector3(2, 0, 2), candidates, avoidTamamizu: false)!.Candidate.Id);
    }

    [Fact]
    public void MissingPositionsUseCheapestGilCostAndPreserveSubIndex()
    {
        var selection = CoppeliaAetherytePolicy.Resolve(
            100,
            new Vector3(5, 0, 5),
            new[]
            {
                Candidate(10, subIndex: 1, gilCost: 500, position: Vector3.Zero),
                Candidate(20, subIndex: 7, gilCost: 100, position: Vector3.Zero),
            },
            avoidTamamizu: true);

        Assert.NotNull(selection);
        Assert.Equal((uint)20, selection.Candidate.Id);
        Assert.Equal((byte)7, selection.Candidate.SubIndex);
        Assert.Equal(double.MaxValue, selection.Distance);
    }

    [Fact]
    public void EmptyLoadingListDiffersFromPopulatedListWithoutTerritory()
    {
        Assert.False(CoppeliaTeleportListPolicy.IsAvailable(false, 0, 0));
        Assert.False(CoppeliaTeleportListPolicy.IsAvailable(true, 0, 0));
        Assert.False(CoppeliaTeleportListPolicy.IsAvailable(true, 2, 0));
        Assert.True(CoppeliaTeleportListPolicy.IsAvailable(true, 2, 2));

        var selection = CoppeliaAetherytePolicy.Resolve(
            200,
            new Vector3(1, 0, 1),
            new[] { Candidate(10, territoryId: 100, position: new Vector3(1, 0, 1)) },
            avoidTamamizu: true);
        Assert.Null(selection);
    }

    [Fact]
    public void PendingExactFallbackRequiresNewerPositionInExactTerritory()
    {
        Assert.True(CoppeliaTeleportIntentPolicy.CanUseFallback(10, 200, 11, 200));
        Assert.False(CoppeliaTeleportIntentPolicy.CanUseFallback(10, 200, 10, 200));
        Assert.False(CoppeliaTeleportIntentPolicy.CanUseFallback(10, 200, 11, 300));
        Assert.True(CoppeliaTeleportIntentPolicy.IsStale(10, 100, 200, 11, 300));
        Assert.False(CoppeliaTeleportIntentPolicy.IsStale(10, 100, 200, 11, 200));
    }

    [Fact]
    public void PassiveArrivalValidationUsesTwentyYalmXzRange()
    {
        Assert.True(CoppeliaAetherytePolicy.IsArrivalWithinRecordingRange(
            new Vector3(12, 999, 16),
            new Vector3(0, 0, 0.01f)));
        Assert.False(CoppeliaAetherytePolicy.IsArrivalWithinRecordingRange(
            new Vector3(20.1f, 0, 0),
            new Vector3(0.01f, 0, 0.01f)));
    }

    [Fact]
    public void TravelSourceRefreshesAndConsumesTeleportListWithoutUnlockApi()
    {
        var testSourcePath = GetThisFilePath();
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testSourcePath)!, ".."));
        var travelSource = File.ReadAllText(Path.Combine(
            repoRoot,
            "Coppelia",
            "Services",
            "CoppeliaTravelService.cs"));

        Assert.DoesNotContain("IsAetheryteUnlocked", travelSource, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(travelSource, "telepo->UpdateAetheryteList();"));
        Assert.Contains("teleportListReader = TryGetTeleportListSnapshot;", travelSource, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(travelSource, "TryGetTeleportListSnapshot("));
        Assert.Contains("TryResolveTeleport(travel, teleportList", travelSource, StringComparison.Ordinal);
        Assert.Contains("TryResolveTeleport(fallbackTravel, teleportList", travelSource, StringComparison.Ordinal);
    }

    private static CoppeliaAetheryteCandidate Candidate(
        uint id,
        byte subIndex = 0,
        uint territoryId = 100,
        string? name = null,
        uint placeNameId = 0,
        uint gilCost = 100,
        Vector3? position = null,
        bool hasStoredPosition = false) =>
        new(
            id,
            subIndex,
            territoryId,
            name ?? $"Aetheryte {id}",
            placeNameId,
            gilCost,
            position ?? Vector3.Zero,
            hasStoredPosition);

    private static int CountOccurrences(string text, string value)
        => text.Split(value, StringSplitOptions.None).Length - 1;

    private static string GetThisFilePath([CallerFilePath] string path = "")
        => path;
}
