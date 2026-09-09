using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coppelia.Models;

internal static class CoppeliaQstContract
{
    public const int Version = 4;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed record CoppeliaQstStatus(
    int ContractVersion,
    bool Compatible,
    bool JoatReady,
    string JoatBlocker,
    bool TravelReady,
    string TravelBlocker,
    bool DafReady,
    string DafBlocker,
    string Mode,
    string Assignment,
    string SessionId,
    string TravelState,
    string DutyState,
    string DutyInviter,
    bool DutyOwned,
    uint? InstanceId = null);

internal sealed record CoppeliaQstCommand(
    string Action,
    string SessionId,
    string QuesterName,
    ushort QuesterWorldId,
    ushort QuesterCurrentWorldId,
    uint TerritoryId,
    float X,
    float Y,
    float Z,
    long TravelSequence,
    uint? AetheryteId,
    byte? AetheryteSubIndex,
    string? AetheryteName,
    bool QuesterMounted,
    bool QuesterFlying,
    bool DutyOptIn,
    string DutyInviter,
    long DutySequence,
    uint DutyTerritoryId,
    uint ContentFinderConditionId,
    int ContractVersion = CoppeliaQstContract.Version,
    uint? InstanceId = null);

internal sealed record CoppeliaQstCommandResponse(
    bool Accepted,
    string Reason,
    int ContractVersion = CoppeliaQstContract.Version);
