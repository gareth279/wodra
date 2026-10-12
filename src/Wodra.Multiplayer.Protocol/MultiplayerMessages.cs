using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wodra.Multiplayer.Protocol;

public static class MultiplayerProtocol
{
    public const int CurrentVersion = 2;
    public const int MaximumMessageBytes = 8 * 1024;
    public const int MinimumUpdateIntervalMs = 75;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] Serialize<T>(T message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, (object?)message is { } value ? value.GetType() : typeof(T), JsonOptions);

    public static ProtocolMessage? Deserialize(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is 0 or > MaximumMessageBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String)
                return null;
            var type = typeValue.GetString();
            ProtocolMessage? message = type switch
            {
                "JoinSession" => JsonSerializer.Deserialize<JoinSessionMessage>(utf8, JsonOptions),
                "JoinAccepted" => JsonSerializer.Deserialize<JoinAcceptedMessage>(utf8, JsonOptions),
                "JoinRejected" => JsonSerializer.Deserialize<JoinRejectedMessage>(utf8, JsonOptions),
                "RiderJoined" => JsonSerializer.Deserialize<RiderJoinedMessage>(utf8, JsonOptions),
                "RiderLeft" => JsonSerializer.Deserialize<RiderLeftMessage>(utf8, JsonOptions),
                "RiderState" => JsonSerializer.Deserialize<RiderStateMessage>(utf8, JsonOptions),
                "RiderRepositioned" => JsonSerializer.Deserialize<RiderRepositionedMessage>(utf8, JsonOptions),
                "LeaveSession" => JsonSerializer.Deserialize<LeaveSessionMessage>(utf8, JsonOptions),
                "Error" => JsonSerializer.Deserialize<ErrorMessage>(utf8, JsonOptions),
                _ => null
            };
            return message?.ProtocolVersion == CurrentVersion ? message : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    public static string? ValidateJoin(JoinSessionMessage join)
    {
        if (join.ProtocolVersion != CurrentVersion) return "Incompatible protocol version.";
        if (!ValidIdentifier(join.RouteId, 1, 80)) return "Invalid route ID.";
        if (join.RouteFingerprint is not { Length: 64 } || !join.RouteFingerprint.All(Uri.IsHexDigit)) return "Invalid route fingerprint.";
        if (!ValidIdentifier(join.RoomCode, 1, 32)) return "Room code must be 1–32 letters, numbers, hyphens or underscores.";
        if (string.IsNullOrWhiteSpace(join.DisplayName) || join.DisplayName.Trim().Length > 32
            || join.DisplayName.Any(char.IsControl)) return "Display name must contain 1–32 visible characters.";
        return null;
    }

    public static string? ValidateState(RiderStateMessage state, string assignedRiderId)
    {
        if (state.ProtocolVersion != CurrentVersion || state.RiderId != assignedRiderId) return "Invalid rider identity.";
        if (state.Sequence < 0 || !double.IsFinite(state.DistanceMeters) || state.DistanceMeters < 0 || state.DistanceMeters > 10_000_000)
            return "Invalid rider distance or sequence.";
        if (!double.IsFinite(state.SpeedKph) || state.SpeedKph < 0 || state.SpeedKph > 360)
            return "Invalid rider speed.";
        if (state.CadenceRpm is < 0 or > 250 || state.PowerWatts is < 0 or > 3000)
            return "Invalid rider telemetry.";
        return null;
    }

    public static string? ValidateReposition(RiderRepositionedMessage state, string assignedRiderId, string routeId, string fingerprint)
    {
        if (!string.Equals(state.RouteId, routeId, StringComparison.Ordinal) ||
            !string.Equals(state.RouteFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            return "Rider reposition does not match the joined route.";
        return ValidateState(new RiderStateMessage { ProtocolVersion = state.ProtocolVersion, RiderId = state.RiderId,
            DisplayName = state.DisplayName, DistanceMeters = state.DistanceMeters, SpeedKph = state.SpeedKph,
            CadenceRpm = state.CadenceRpm, PowerWatts = state.PowerWatts, Paused = state.Paused,
            Sequence = state.Sequence }, assignedRiderId);
    }

    private static bool ValidIdentifier(string? value, int min, int max) =>
        value is not null && value.Length >= min && value.Length <= max
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

public abstract record ProtocolMessage
{
    public int ProtocolVersion { get; init; } = MultiplayerProtocol.CurrentVersion;
    public abstract string Type { get; }
}

public sealed record JoinSessionMessage : ProtocolMessage
{
    public override string Type => "JoinSession";
    public string RouteId { get; init; } = "";
    public string RouteFingerprint { get; init; } = "";
    public string RoomCode { get; init; } = "";
    public string DisplayName { get; init; } = "";
}

public sealed record JoinAcceptedMessage : ProtocolMessage
{
    public override string Type => "JoinAccepted";
    public string RiderId { get; init; } = "";
    public string RouteId { get; init; } = "";
    public string RouteFingerprint { get; init; } = "";
    public string RoomCode { get; init; } = "";
}

public sealed record JoinRejectedMessage : ProtocolMessage
{
    public override string Type => "JoinRejected";
    public string Reason { get; init; } = "Join rejected.";
}

public sealed record RiderJoinedMessage : ProtocolMessage
{
    public override string Type => "RiderJoined";
    public RiderDescriptor Rider { get; init; } = new();
}

public sealed record RiderLeftMessage : ProtocolMessage
{
    public override string Type => "RiderLeft";
    public string RiderId { get; init; } = "";
    public string Reason { get; init; } = "left";
}

public sealed record RiderStateMessage : ProtocolMessage
{
    public override string Type => "RiderState";
    public string RiderId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public double DistanceMeters { get; init; }
    public double SpeedKph { get; init; }
    public int CadenceRpm { get; init; }
    public int PowerWatts { get; init; }
    public bool Paused { get; init; }
    public long Sequence { get; init; }
}

public sealed record RiderRepositionedMessage : ProtocolMessage
{
    public override string Type => "RiderRepositioned";
    public string RouteId { get; init; } = "";
    public string RouteFingerprint { get; init; } = "";
    public string RiderId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public double DistanceMeters { get; init; }
    public double SpeedKph { get; init; }
    public int CadenceRpm { get; init; }
    public int PowerWatts { get; init; }
    public bool Paused { get; init; }
    public long Sequence { get; init; }
}

public sealed record RiderDescriptor
{
    public string RiderId { get; init; } = "";
    public string DisplayName { get; init; } = "";
}

public sealed record LeaveSessionMessage : ProtocolMessage
{
    public override string Type => "LeaveSession";
}

public sealed record ErrorMessage : ProtocolMessage
{
    public override string Type => "Error";
    public string Code { get; init; } = "invalid_message";
    public string Message { get; init; } = "Invalid message.";
}
