using System.Text.Json;
using Coppelia.Models;

namespace Coppelia.Services;

internal sealed class FrenRiderPowerlevelIpcService
{
    private const string AcquireEndpoint = "FrenRider.Coppelia.Powerlevel.Acquire";
    private const string HeartbeatEndpoint = "FrenRider.Coppelia.Powerlevel.Heartbeat";
    private const string ReleaseEndpoint = "FrenRider.Coppelia.Powerlevel.Release";
    private const string StatusEndpoint = "FrenRider.Coppelia.Powerlevel.Status";

    private DateTimeOffset nextHeartbeatUtc = DateTimeOffset.MinValue;
    private string sessionToken = Guid.NewGuid().ToString("N");
    private readonly Func<string, string, string> invokeEndpoint;
    private bool heartbeatHealthy;
    private bool combatSuppressed;
    private bool acquireUncertain;

    public FrenRiderPowerlevelIpcService(Func<string, string, string>? invokeEndpoint = null)
        => this.invokeEndpoint = invokeEndpoint ?? ((endpoint, json) => Plugin.PluginInterface
            .GetIpcSubscriber<string, string>(endpoint).InvokeFunc(json));

    public bool LeaseAcquired { get; private set; }
    public bool CombatSuppressed => LeaseAcquired && heartbeatHealthy && combatSuppressed;
    public FrenRiderPowerlevelStatus? LastStatus { get; private set; }
    public string LastFailure { get; private set; } = "FrenRider Powerlevel IPC has not been queried yet.";

    public bool ResetSession()
    {
        if ((LeaseAcquired || acquireUncertain) && !Release("session reset"))
            return false;

        sessionToken = Guid.NewGuid().ToString("N");
        nextHeartbeatUtc = DateTimeOffset.MinValue;
        LeaseAcquired = false;
        heartbeatHealthy = false;
        combatSuppressed = false;
        acquireUncertain = false;
        return true;
    }

    public bool TryGetStatus(out FrenRiderPowerlevelStatus status)
    {
        try
        {
            var json = Plugin.PluginInterface
                .GetIpcSubscriber<string>(StatusEndpoint)
                .InvokeFunc();
            status = JsonSerializer.Deserialize<FrenRiderPowerlevelStatus>(
                         json,
                         FrenRiderPowerlevelContract.JsonOptions) ??
                     BuildUnavailableStatus("Empty FrenRider Powerlevel status.");
            LastStatus = status;
            if (!status.IsCompatible)
                LastFailure = "Incompatible FrenRider Powerlevel status.";
            else if (!LeaseAcquired || heartbeatHealthy)
                LastFailure = string.Empty;
            return status.IsCompatible;
        }
        catch (Exception ex)
        {
            status = BuildUnavailableStatus(ex.Message);
            LastStatus = status;
            LastFailure = $"FrenRider Powerlevel status IPC failed: {ex.Message}";
            return false;
        }
    }

    public bool Acquire(out string failureReason)
    {
        var response = InvokeSessionEndpoint(AcquireEndpoint, out failureReason);
        if (response?.Ok == true)
        {
            LeaseAcquired = true;
            heartbeatHealthy = true;
            combatSuppressed = response.CombatSuppressed;
            acquireUncertain = false;
            nextHeartbeatUtc = DateTimeOffset.UtcNow.AddSeconds(1);
            return true;
        }

        // The provider may have accepted Acquire before the transport failed.
        // Keep this token eligible for explicit cleanup even without an ack.
        acquireUncertain |= response == null;
        LeaseAcquired = false;
        heartbeatHealthy = false;
        combatSuppressed = false;
        return false;
    }

    public bool HeartbeatIfDue(out string failureReason)
    {
        failureReason = string.Empty;
        if (!LeaseAcquired)
            return true;

        if (DateTimeOffset.UtcNow < nextHeartbeatUtc)
        {
            failureReason = heartbeatHealthy ? string.Empty : LastFailure;
            return heartbeatHealthy;
        }

        var response = InvokeSessionEndpoint(HeartbeatEndpoint, out failureReason);
        nextHeartbeatUtc = DateTimeOffset.UtcNow.AddSeconds(1);
        if (response?.Ok == true)
        {
            heartbeatHealthy = true;
            combatSuppressed = response.CombatSuppressed;
            return true;
        }

        // Keep the token for acknowledged cleanup; a failed transport call does not
        // prove the provider released its lease. Damage stays blocked meanwhile.
        heartbeatHealthy = false;
        combatSuppressed = false;
        return false;
    }

    public bool Release(string reason)
    {
        if (!LeaseAcquired && !acquireUncertain)
            return true;

        var response = InvokeSessionEndpoint(ReleaseEndpoint, out _);
        if (response?.Ok != true)
            return false;
        LeaseAcquired = false;
        heartbeatHealthy = false;
        combatSuppressed = false;
        acquireUncertain = false;
        nextHeartbeatUtc = DateTimeOffset.MinValue;
        LastFailure = reason;
        return true;
    }

    private FrenRiderPowerlevelResponse? InvokeSessionEndpoint(string endpoint, out string failureReason)
    {
        failureReason = string.Empty;
        try
        {
            var request = JsonSerializer.Serialize(
                new FrenRiderPowerlevelRequest(sessionToken),
                FrenRiderPowerlevelContract.JsonOptions);
            var json = invokeEndpoint(endpoint, request);
            var response = JsonSerializer.Deserialize<FrenRiderPowerlevelResponse>(
                json,
                FrenRiderPowerlevelContract.JsonOptions);
            if (response != null && response.ContractVersion != FrenRiderPowerlevelContract.SupportedVersion)
                response = response with { Ok = false, CombatSuppressed = false, Reason = "Incompatible FrenRider Powerlevel response." };
            failureReason = response?.Reason ?? "FrenRider returned an empty response.";
            LastFailure = response?.Ok == true ? string.Empty : failureReason;
            return response;
        }
        catch (Exception ex)
        {
            failureReason = $"FrenRider Powerlevel IPC failed: {ex.Message}";
            LastFailure = failureReason;
            return null;
        }
    }

    private static FrenRiderPowerlevelStatus BuildUnavailableStatus(string reason)
        => new(
            ContractVersion: 0,
            LeaseActive: false,
            LeaseReason: reason,
            FrenRiderEnabled: false,
            ConfiguredFrenName: string.Empty,
            VisibleFrenName: string.Empty,
            VisibleFrenObjectId: 0,
            CompanionActive: false,
            CompanionName: string.Empty,
            CompanionObjectId: 0);
}
