using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coppelia.Models;

internal enum HealBotLanMessageType
{
    StatusRequest,
    StatusResponse,
    AssignNewb,
    TravelUpdate,
    Release,
    CommandResult,
}

internal sealed class HealBotLanEnvelope
{
    public const int CurrentProtocolVersion = 1;
    public const int MaximumTcpFrameBytes = 64 * 1024;

    public int ProtocolVersion { get; set; } = CurrentProtocolVersion;
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    public string UtcTimestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    public HealBotLanMessageType Type { get; set; }
    public string? Data { get; set; }
    public string AuthenticationTag { get; set; } = string.Empty;

    public static HealBotLanEnvelope Create(HealBotLanMessageType type, object? data = null)
        => new()
        {
            Type = type,
            Data = data == null ? null : JsonSerializer.Serialize(data, HealBotLanContract.JsonOptions),
        };

    public T? GetData<T>()
        => string.IsNullOrEmpty(Data)
            ? default
            : JsonSerializer.Deserialize<T>(Data, HealBotLanContract.JsonOptions);
}

internal static class HealBotLanContract
{
    private static readonly TimeSpan AllowedClockSkew = TimeSpan.FromMinutes(2);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Sign(HealBotLanEnvelope message, string secret)
    {
        if (secret.Length < 16)
            throw new InvalidOperationException("The pair secret must contain at least 16 characters.");

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        message.AuthenticationTag = Convert.ToBase64String(hmac.ComputeHash(BuildAuthenticatedBytes(message)));
    }

    public static bool TryValidate(
        HealBotLanEnvelope message,
        string secret,
        HealBotLanReplayCache replayCache,
        out string failure)
    {
        failure = string.Empty;
        if (secret.Length < 16)
        {
            failure = "The pair secret is not configured.";
            return false;
        }

        if (message.ProtocolVersion != HealBotLanEnvelope.CurrentProtocolVersion ||
            string.IsNullOrWhiteSpace(message.MessageId) ||
            message.MessageId.Length > 128 ||
            string.IsNullOrWhiteSpace(message.UtcTimestamp) ||
            string.IsNullOrWhiteSpace(message.AuthenticationTag))
        {
            failure = "Unsigned or incompatible pairing envelope.";
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                message.UtcTimestamp,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            failure = "Invalid pairing timestamp.";
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if ((now - timestamp).Duration() > AllowedClockSkew)
        {
            failure = "Stale pairing envelope.";
            return false;
        }

        byte[] supplied;
        try
        {
            supplied = Convert.FromBase64String(message.AuthenticationTag);
        }
        catch (FormatException)
        {
            failure = "Invalid pairing authentication tag.";
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = hmac.ComputeHash(BuildAuthenticatedBytes(message));
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            failure = "Pairing authentication failed.";
            return false;
        }

        if (!replayCache.TryAccept(message.MessageId, now))
        {
            failure = "Replayed pairing envelope.";
            return false;
        }

        return true;
    }

    public static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var singleByte = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(singleByte.AsMemory(0, 1), cancellationToken);
            if (read == 0)
                return buffer.Length == 0 ? null : throw new InvalidDataException("Incomplete pairing TCP frame.");

            if (singleByte[0] == (byte)'\n')
                return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');

            if (buffer.Length >= HealBotLanEnvelope.MaximumTcpFrameBytes)
                throw new InvalidDataException("Pairing TCP frame exceeds 64 KiB.");

            buffer.WriteByte(singleByte[0]);
        }
    }

    public static byte[] SerializeFrame(HealBotLanEnvelope message, string secret)
    {
        Sign(message, secret);
        var json = JsonSerializer.Serialize(message, JsonOptions);
        var payload = Encoding.UTF8.GetBytes(json + "\n");
        if (payload.Length > HealBotLanEnvelope.MaximumTcpFrameBytes + 1)
            throw new InvalidDataException("Pairing TCP frame exceeds 64 KiB.");
        return payload;
    }

    private static byte[] BuildAuthenticatedBytes(HealBotLanEnvelope message)
    {
        var canonical = string.Join(
            "\n",
            message.ProtocolVersion.ToString(CultureInfo.InvariantCulture),
            message.MessageId,
            message.UtcTimestamp,
            ((int)message.Type).ToString(CultureInfo.InvariantCulture),
            message.Data ?? string.Empty);
        return Encoding.UTF8.GetBytes(canonical);
    }
}

internal sealed class HealBotLanReplayCache
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> acceptedMessages = new(StringComparer.Ordinal);

    public bool TryAccept(string messageId, DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (var stale in acceptedMessages
                         .Where(pair => now - pair.Value > Retention)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                acceptedMessages.Remove(stale);
            }

            if (acceptedMessages.ContainsKey(messageId))
                return false;

            acceptedMessages[messageId] = now;
            return true;
        }
    }

    public void Clear()
    {
        lock (gate)
            acceptedMessages.Clear();
    }
}

internal sealed record HealBotLanStatusRequest(int PairProtocolVersion = HealBotLanEnvelope.CurrentProtocolVersion);

internal sealed record HealBotLanStatusResponse(
    string RequestId,
    int PairProtocolVersion,
    string HealBotName,
    ushort HealBotWorldId,
    bool Ready,
    string Blocker,
    string Mode,
    bool AutomationEnabled,
    string AssignmentSource,
    string AssignedName,
    ushort AssignedWorldId,
    string SessionId,
    string RuntimeState,
    string HealingState,
    string ChaseState);

internal sealed record HealBotLanTargetedCommand(
    string TargetHealBotName,
    ushort TargetHealBotWorldId,
    HealBotLanCommand Command);

internal sealed record HealBotLanCommand(
    string Action,
    string SessionId,
    string NewbName,
    ushort NewbWorldId,
    ushort NewbCurrentWorldId,
    uint TerritoryId,
    float X,
    float Y,
    float Z,
    long TravelSequence,
    uint? AetheryteId,
    byte? AetheryteSubIndex,
    string? AetheryteName,
    bool NewbMounted,
    bool NewbFlying)
{
    public static readonly HealBotLanCommand Empty = new(
        string.Empty,
        string.Empty,
        string.Empty,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        null,
        null,
        null,
        false,
        false);
}

internal sealed record HealBotLanCommandResult(
    string RequestId,
    string Action,
    string SessionId,
    bool Accepted,
    string Blocker)
{
    public static HealBotLanCommandResult Rejected(
        string requestId,
        HealBotLanTargetedCommand? targeted,
        string blocker)
        => new(
            requestId,
            targeted?.Command.Action ?? string.Empty,
            targeted?.Command.SessionId ?? string.Empty,
            false,
            blocker);
}
