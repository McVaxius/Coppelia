using System.Text.Json;
using Coppelia.Models;
using Coppelia.Services;

namespace Coppelia.Tests;

public sealed class FrenRiderPowerlevelContractTests
{
    [Fact]
    public void UncertainAcquireRetainsItsTokenForAcknowledgedCleanupBeforeReset()
    {
        var releaseAllowed = false;
        var tokens = new List<string>();
        var service = new FrenRiderPowerlevelIpcService((endpoint, json) =>
        {
            tokens.Add(JsonSerializer.Deserialize<FrenRiderPowerlevelRequest>(json,
                FrenRiderPowerlevelContract.JsonOptions)!.SessionToken);
            if (endpoint.EndsWith("Acquire", StringComparison.Ordinal))
                throw new InvalidOperationException("Acquire response unavailable");
            return JsonSerializer.Serialize(new FrenRiderPowerlevelResponse(releaseAllowed, "release result"),
                FrenRiderPowerlevelContract.JsonOptions);
        });
        Assert.False(service.Acquire(out _));
        Assert.False(service.LeaseAcquired);
        Assert.False(service.CombatSuppressed);
        Assert.False(service.ResetSession());
        Assert.Equal(2, tokens.Count);
        Assert.Single(tokens.Distinct(StringComparer.Ordinal));
        releaseAllowed = true;
        Assert.True(service.ResetSession());
        Assert.Equal(3, tokens.Count);
        Assert.Single(tokens.Distinct(StringComparer.Ordinal));
        Assert.False(service.Acquire(out _));
        Assert.NotEqual(tokens[0], tokens[^1]);
    }

    [Fact]
    public void LegacyAcquireWithoutSuppressionAcknowledgementCannotEnableBmrHandoff()
    {
        var service = new FrenRiderPowerlevelIpcService((_, _) =>
            """{"ok":true,"reason":"acquired","contractVersion":1}""");
        var runtime = new JotRuntimeService(null!, service);
        Assert.True(service.Acquire(out _));
        Assert.True(service.LeaseAcquired);
        Assert.False(service.CombatSuppressed);
        Assert.False(runtime.IsBmrHandoffReady);
        Assert.Contains("acknowledgement", runtime.BmrHandoffBlocker, StringComparison.Ordinal);
    }

    [Fact]
    public void BmrHandoffRequiresSuppressionInEverySuccessfulSessionResponse()
    {
        var suppressed = true;
        var service = new FrenRiderPowerlevelIpcService((_, _) => JsonSerializer.Serialize(
            new FrenRiderPowerlevelResponse(true, "active", CombatSuppressed: suppressed),
            FrenRiderPowerlevelContract.JsonOptions));
        var runtime = new JotRuntimeService(null!, service);
        Assert.True(service.Acquire(out _));
        Assert.True(runtime.IsBmrHandoffReady);
        suppressed = false;
        SetHeartbeatDue(service);
        Assert.True(service.HeartbeatIfDue(out _));
        Assert.False(runtime.IsBmrHandoffReady);
        Assert.True(service.LeaseAcquired);
        suppressed = true;
        SetHeartbeatDue(service);
        Assert.True(service.HeartbeatIfDue(out _));
        Assert.True(runtime.IsBmrHandoffReady);
        Assert.True(service.Release("done"));
        Assert.False(runtime.IsBmrHandoffReady);
    }

    [Fact]
    public void IncompatibleSessionResponseCannotAcknowledgeBmrHandoff()
    {
        var service = new FrenRiderPowerlevelIpcService((_, _) => JsonSerializer.Serialize(
            new FrenRiderPowerlevelResponse(true, "active", ContractVersion: 2, CombatSuppressed: true),
            FrenRiderPowerlevelContract.JsonOptions));
        Assert.False(service.Acquire(out _));
        Assert.False(service.CombatSuppressed);
        Assert.Contains("Incompatible", service.LastFailure, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingRestorationKeepsHeartbeatAndNeverReleasesOrEnablesHandoff()
    {
        var endpoints = new List<string>();
        var service = new FrenRiderPowerlevelIpcService((endpoint, _) =>
        {
            endpoints.Add(endpoint);
            return JsonSerializer.Serialize(new FrenRiderPowerlevelResponse(true, "active", CombatSuppressed: true),
                FrenRiderPowerlevelContract.JsonOptions);
        });
        var runtime = new JotRuntimeService(null!, service);
        Assert.True(service.Acquire(out _));
        typeof(JotRuntimeService).GetField("restorationPending",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(runtime, true);
        SetHeartbeatDue(service);
        runtime.PrepareHealingCycle();
        SetHeartbeatDue(service);
        runtime.Update(HealbotDecisionOutcome.Unavailable);
        Assert.True(service.LeaseAcquired);
        Assert.False(runtime.IsBmrHandoffReady);
        Assert.Equal(2, endpoints.Count(endpoint => endpoint.EndsWith("Heartbeat", StringComparison.Ordinal)));
        Assert.DoesNotContain(endpoints, endpoint => endpoint.EndsWith("Release", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedHeartbeatRetainsCleanupOwnershipAndBlocksUntilAnAcknowledgedHeartbeat()
    {
        var heartbeatAllowed = false;
        var calls = 0;
        var service = new FrenRiderPowerlevelIpcService((endpoint, _) =>
        {
            calls++;
            return JsonSerializer.Serialize(new FrenRiderPowerlevelResponse(
                !endpoint.EndsWith("Heartbeat", StringComparison.Ordinal) || heartbeatAllowed, "heartbeat result"),
                FrenRiderPowerlevelContract.JsonOptions);
        });
        var nextHeartbeat = typeof(FrenRiderPowerlevelIpcService).GetField("nextHeartbeatUtc",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.True(service.Acquire(out _));
        nextHeartbeat.SetValue(service, DateTimeOffset.MinValue);
        Assert.False(service.HeartbeatIfDue(out _));
        Assert.True(service.LeaseAcquired);
        var afterFailure = calls;
        Assert.False(service.HeartbeatIfDue(out _));
        Assert.Equal(afterFailure, calls);
        heartbeatAllowed = true;
        nextHeartbeat.SetValue(service, DateTimeOffset.MinValue);
        Assert.True(service.HeartbeatIfDue(out _));
        Assert.True(service.Release("stop"));
        Assert.False(service.LeaseAcquired);
    }

    [Fact]
    public void RejectedReleaseRetainsLeaseAndTokenUntilAcknowledged()
    {
        var releaseAllowed = false;
        var tokens = new List<string>();
        var service = new FrenRiderPowerlevelIpcService((endpoint, request) =>
        {
            tokens.Add(JsonSerializer.Deserialize<FrenRiderPowerlevelRequest>(request,
                FrenRiderPowerlevelContract.JsonOptions)!.SessionToken);
            return JsonSerializer.Serialize(new FrenRiderPowerlevelResponse(
                !endpoint.EndsWith("Release", StringComparison.Ordinal) || releaseAllowed, "lease result"),
                FrenRiderPowerlevelContract.JsonOptions);
        });

        Assert.True(service.Acquire(out _));
        Assert.False(service.Release("stop"));
        Assert.True(service.LeaseAcquired);
        Assert.False(service.ResetSession());
        Assert.True(service.LeaseAcquired);
        Assert.Single(tokens.Distinct(StringComparer.Ordinal));

        releaseAllowed = true;
        Assert.True(service.ResetSession());
        Assert.False(service.LeaseAcquired);
        Assert.True(service.Acquire(out _));
        Assert.NotEqual(tokens[0], tokens[^1]);
    }

    [Fact]
    public void ReleaseTransportFailureDoesNotClaimTheLeaseWasReleased()
    {
        var service = new FrenRiderPowerlevelIpcService((endpoint, _) =>
            endpoint.EndsWith("Release", StringComparison.Ordinal)
                ? throw new InvalidOperationException("release unavailable")
                : JsonSerializer.Serialize(new FrenRiderPowerlevelResponse(true, "acquired"), FrenRiderPowerlevelContract.JsonOptions));
        Assert.True(service.Acquire(out _));
        Assert.False(service.Release("stop"));
        Assert.True(service.LeaseAcquired);
        Assert.Contains("release unavailable", service.LastFailure, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusJsonReportsCompatibilityAndFrenState()
    {
        var json = """
                   {
                     "contractVersion": 1,
                     "leaseActive": true,
                     "leaseReason": "active",
                     "frenRiderEnabled": true,
                     "configuredFrenName": "Leader",
                     "visibleFrenName": "Leader",
                     "visibleFrenObjectId": 123,
                     "companionActive": false,
                     "companionName": "",
                     "companionObjectId": 0
                   }
                   """;

        var status = JsonSerializer.Deserialize<FrenRiderPowerlevelStatus>(
            json,
            FrenRiderPowerlevelContract.JsonOptions);

        Assert.NotNull(status);
        Assert.True(status!.IsCompatible);
        Assert.True(status.FrenConfigured);
        Assert.True(status.FrenVisible);
    }

    [Fact]
    public void ResponseJsonPreservesFailureReason()
    {
        var json = """{"ok":false,"reason":"owned","contractVersion":1}""";

        var response = JsonSerializer.Deserialize<FrenRiderPowerlevelResponse>(
            json,
            FrenRiderPowerlevelContract.JsonOptions);

        Assert.NotNull(response);
        Assert.False(response!.Ok);
        Assert.Equal("owned", response.Reason);
    }

    private static void SetHeartbeatDue(FrenRiderPowerlevelIpcService service)
        => typeof(FrenRiderPowerlevelIpcService).GetField("nextHeartbeatUtc",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(service, DateTimeOffset.MinValue);
}
