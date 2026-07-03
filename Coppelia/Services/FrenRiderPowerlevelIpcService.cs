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

    public bool LeaseAcquired { get; private set; }
    public FrenRiderPowerlevelStatus? LastStatus { get; private set; }
    public string LastFailure { get; private set; } = "FrenRider Powerlevel IPC has not been queried yet.";

    public void ResetSession()
    {
        if (LeaseAcquired)
            Release("session reset");

        sessionToken = Guid.NewGuid().ToString("N");
        nextHeartbeatUtc = DateTimeOffset.MinValue;
        LeaseAcquired = false;
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
            LastFailure = status.IsCompatible ? string.Empty : "Incompatible FrenRider Powerlevel status.";
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
            nextHeartbeatUtc = DateTimeOffset.UtcNow.AddSeconds(1);
            return true;
        }

        LeaseAcquired = false;
        return false;
    }

    public bool HeartbeatIfDue(out string failureReason)
    {
        failureReason = string.Empty;
        if (!LeaseAcquired)
            return true;

        if (DateTimeOffset.UtcNow < nextHeartbeatUtc)
            return true;

        var response = InvokeSessionEndpoint(HeartbeatEndpoint, out failureReason);
        if (response?.Ok == true)
        {
            nextHeartbeatUtc = DateTimeOffset.UtcNow.AddSeconds(1);
            return true;
        }

        LeaseAcquired = false;
        return false;
    }

    public void Release(string reason)
    {
        if (!LeaseAcquired)
            return;

        InvokeSessionEndpoint(ReleaseEndpoint, out _);
        LeaseAcquired = false;
        nextHeartbeatUtc = DateTimeOffset.MinValue;
        LastFailure = reason;
    }

    private FrenRiderPowerlevelResponse? InvokeSessionEndpoint(string endpoint, out string failureReason)
    {
        failureReason = string.Empty;
        try
        {
            var request = JsonSerializer.Serialize(
                new FrenRiderPowerlevelRequest(sessionToken),
                FrenRiderPowerlevelContract.JsonOptions);
            var json = Plugin.PluginInterface
                .GetIpcSubscriber<string, string>(endpoint)
                .InvokeFunc(request);
            var response = JsonSerializer.Deserialize<FrenRiderPowerlevelResponse>(
                json,
                FrenRiderPowerlevelContract.JsonOptions);
            failureReason = response?.Reason ?? "FrenRider returned an empty response.";
            LastFailure = failureReason;
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
