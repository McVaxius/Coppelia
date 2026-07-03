using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coppelia.Models;

internal static class FrenRiderPowerlevelContract
{
    public const int SupportedVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed record FrenRiderPowerlevelRequest(string SessionToken);

internal sealed record FrenRiderPowerlevelResponse(
    bool Ok,
    string Reason,
    int ContractVersion = FrenRiderPowerlevelContract.SupportedVersion);

internal sealed record FrenRiderPowerlevelStatus(
    int ContractVersion,
    bool LeaseActive,
    string LeaseReason,
    bool FrenRiderEnabled,
    string ConfiguredFrenName,
    string VisibleFrenName,
    ulong VisibleFrenObjectId,
    bool CompanionActive,
    string CompanionName,
    ulong CompanionObjectId)
{
    public bool IsCompatible => ContractVersion == FrenRiderPowerlevelContract.SupportedVersion;
    public bool FrenConfigured => !string.IsNullOrWhiteSpace(ConfiguredFrenName);
    public bool FrenVisible => VisibleFrenObjectId != 0 && !string.IsNullOrWhiteSpace(VisibleFrenName);
}
