using IndoorCycling.Core.Models;

namespace IndoorCycling.Trainer.Models;

public readonly record struct AntFeCDeviceIdentity(ushort DeviceNumber, byte DeviceType, byte TransmissionType)
{
    public string DisplayName => $"ANT+ FE-C trainer {DeviceNumber}";
}

public readonly record struct AntFeCReceiveChannelSettings(ushort DeviceNumber, bool PairingEnabled,
    byte DeviceType, byte TransmissionType, ushort Period, byte RadioFrequency);

/// <summary>Decoded ANT PC SDK receive callback frame metadata, with no raw payload retention.</summary>
public readonly record struct AntReceiveFrame(byte MessageId, bool IsExtended, int PayloadLength,
    byte? DataPage, AntFeCDeviceIdentity? Source, string? RejectionReason);

/// <summary>Parses the messageContents layout used by ANT PC SDK ANT_Response.</summary>
public static class AntFeCScanFrameParser
{
    public static AntReceiveFrame Parse(byte messageId, ReadOnlySpan<byte> messageContents,
        AntFeCDeviceIdentity? channelIdentity = null)
    {
        var isExtended = messageContents.Length >= 13;
        var isLegacyData = messageId is 0x4E or 0x4F or 0x50;
        var isExtendedData = messageId is 0x5D or 0x5E or 0x5F;
        if (!isLegacyData && !isExtendedData)
            return new(messageId, isExtended, 0, null, null, "unsupported_message_id");

        var payloadOffset = isExtendedData ? 5 : 1;
        var hasExtendedId = isExtendedData && isExtended || isLegacyData && isExtended &&
            messageContents.Length >= 14 && (messageContents[9] & 0x80) != 0;
        if (messageContents.Length < payloadOffset + 8)
            return new(messageId, isExtended, Math.Max(0, messageContents.Length - payloadOffset), null, null, "payload_too_short");

        var sourceOffset = isExtendedData ? 1 : 10;
        AntFeCDeviceIdentity? source = null;
        if (hasExtendedId && messageContents.Length >= sourceOffset + 4)
        {
            source = new((ushort)(messageContents[sourceOffset] | messageContents[sourceOffset + 1] << 8),
                (byte)(messageContents[sourceOffset + 2] & 0x7F), messageContents[sourceOffset + 3]);
        }
        else if (isExtendedData)
            return new(messageId, isExtended, 8, messageContents[payloadOffset], null, "extended_source_id_missing");

        var page = messageContents[payloadOffset];
        var payloadLength = Math.Min(8, messageContents.Length - payloadOffset);
        if (payloadLength != 8) return new(messageId, isExtended, payloadLength, page, source, "payload_length_not_8");
        if (page is not (AntFeCProtocol.PageGeneral or AntFeCProtocol.PageTrainer or AntFeCProtocol.PageCapabilities))
            return new(messageId, isExtended, payloadLength, page, source, "unsupported_page");
        source ??= channelIdentity;
        if (source is not null && source.Value.DeviceType != AntFeCProtocol.DeviceType)
            return new(messageId, isExtended, payloadLength, page, source, "device_type_mismatch");
        if (source is { DeviceNumber: 0 })
            return new(messageId, isExtended, payloadLength, page, source, "device_number_zero");
        return new(messageId, isExtended, payloadLength, page, source, source is null ? "source_id_missing" : null);
    }

    /// <summary>Reads the source identity reported by ANT for a wildcard receive channel.</summary>
    public static AntFeCDeviceIdentity? ParseChannelIdentity(byte messageId, ReadOnlySpan<byte> messageContents)
    {
        if (messageId != 0x51 || messageContents.Length < 5) return null;
        var number = (ushort)(messageContents[1] | messageContents[2] << 8);
        var type = (byte)(messageContents[3] & 0x7F);
        var transmission = messageContents[4];
        return number == 0 ? null : new(number, type, transmission);
    }
}

/// <summary>Bounded aggregation for scan callback metadata. Does not retain payload bytes.</summary>
public sealed class AntReceiveDiagnosticCounts
{
    private const int MaximumBuckets = 64;
    private readonly Dictionary<(byte MessageId, string Source, bool Extended, int Length, byte? Page, string Reason), int> _counts = [];
    private int _overflow;

    public void Add(byte messageId, string callbackSource, bool extended, int payloadLength, byte? dataPage, string rejectionReason)
    {
        var key = (messageId, callbackSource, extended, payloadLength, dataPage, rejectionReason);
        if (_counts.TryGetValue(key, out var count)) _counts[key] = count + 1;
        else if (_counts.Count < MaximumBuckets) _counts.Add(key, 1);
        else _overflow++;
    }

    public IEnumerable<(byte MessageId, string CallbackSource, bool Extended, int PayloadLength, byte? DataPage, string RejectionReason, int Count)> Snapshot()
    {
        foreach (var (key, count) in _counts)
            yield return (key.MessageId, key.Source, key.Extended, key.Length, key.Page, key.Reason, count);
        if (_overflow > 0) yield return (0, "multiple", false, 0, null, "diagnostic_bucket_limit", _overflow);
    }
}

public enum AntFeCDiscoveryOutcome
{
    None,
    Connect,
    Choose
}

public sealed record AntFeCDiscoverySelection(AntFeCDiscoveryOutcome Outcome,
    IReadOnlyList<AntFeCDeviceIdentity> Candidates, AntFeCDeviceIdentity? Selected);

public static class AntFeCDiscoveryPolicy
{
    public static AntFeCDiscoverySelection Select(IEnumerable<AntFeCDeviceIdentity> discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        var candidates = discovered.Where(device => device.DeviceNumber != 0 && device.DeviceType == AntFeCProtocol.DeviceType)
            .Distinct().OrderBy(device => device.DeviceNumber).ThenBy(device => device.TransmissionType).ToArray();
        return candidates.Length switch
        {
            0 => new(AntFeCDiscoveryOutcome.None, candidates, null),
            1 => new(AntFeCDiscoveryOutcome.Connect, candidates, candidates[0]),
            _ => new(AntFeCDiscoveryOutcome.Choose, candidates, null)
        };
    }
}

/// <summary>FE-C page layouts and conversions from ANT+ Fitness Equipment Profile revision 5.</summary>
public static class AntFeCProtocol
{
    public const byte DeviceType = 17;
    public const ushort ChannelPeriod = 8192;
    public const byte RadioFrequency = 57;
    public const byte PageGeneral = 16;
    public const byte PageTrainer = 25;
    public const byte PageCapabilities = 54;
    public const byte PageCommandStatus = 71;
    public const byte PageRequest = 70;
    public const int MaximumCapabilityRequestAttempts = 3;
    public static AntFeCReceiveChannelSettings DiscoveryChannel => new(
        DeviceNumber: 0, PairingEnabled: false, DeviceType: DeviceType, TransmissionType: 0,
        Period: ChannelPeriod, RadioFrequency: RadioFrequency);

    public static bool IsReady(bool hasGeneralPage, bool hasTrainerPage, bool hasCapabilities) =>
        hasGeneralPage && hasTrainerPage && hasCapabilities;

    public static bool ShouldRetryCapabilityRequest(bool hasCapabilities, int attempts) =>
        !hasCapabilities && attempts < MaximumCapabilityRequestAttempts;

    public static byte[] BuildPageRequest(byte requestedPage) =>
        [PageRequest, 0xFF, 0x01, 0x01, 0xFF, 0xFF, requestedPage, 0x01];

    public static byte[] BuildTrackResistance(double gradePercent, double rollingResistance = 0.004)
    {
        if (!double.IsFinite(gradePercent) || !double.IsFinite(rollingResistance))
            throw new ArgumentOutOfRangeException(nameof(gradePercent));
        var grade = (ushort)Math.Clamp((int)Math.Round((Math.Clamp(gradePercent, -200, 200) + 200) * 100), 0, 40000);
        var crr = (byte)Math.Clamp((int)Math.Round(Math.Clamp(rollingResistance, 0, 0.0127) / 0.00005), 0, 254);
        return [51, 0xFF, 0xFF, 0xFF, 0xFF, (byte)grade, (byte)(grade >> 8), crr];
    }

    public static byte[] BuildTargetPower(double watts)
    {
        if (!double.IsFinite(watts)) throw new ArgumentOutOfRangeException(nameof(watts));
        var raw = (ushort)Math.Clamp((int)Math.Round(Math.Clamp(watts, 0, 1000) * 4), 0, 4000);
        return [49, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, (byte)raw, (byte)(raw >> 8)];
    }

    public static byte[] BuildUserConfiguration(double riderWeightKg, double bicycleWeightKg)
    {
        if (!double.IsFinite(riderWeightKg) || !double.IsFinite(bicycleWeightKg))
            throw new ArgumentOutOfRangeException(nameof(riderWeightKg));
        var rider = (ushort)Math.Clamp((int)Math.Round(Math.Clamp(riderWeightKg, 0, 655.34) * 100), 0, 65534);
        var bike = (ushort)Math.Clamp((int)Math.Round(Math.Clamp(bicycleWeightKg, 0, 50) * 20), 0, 1000);
        return [55, (byte)rider, (byte)(rider >> 8), 0xFF, (byte)(0x0F | ((bike & 0x0F) << 4)),
            (byte)(bike >> 4), 0xFF, 0x00];
    }

    public static TrainerTelemetry DecodeTelemetry(ReadOnlySpan<byte> page, DateTimeOffset timestamp,
        AntFeCPowerAccumulator accumulator)
    {
        if (page.Length != 8) throw new ArgumentException("FE-C data page must contain 8 bytes.", nameof(page));
        return page[0] switch
        {
            PageGeneral => DecodeGeneral(page, timestamp),
            PageTrainer => DecodeTrainer(page, timestamp, accumulator),
            _ => throw new ArgumentOutOfRangeException(nameof(page), "Unsupported FE-C telemetry page.")
        };
    }

    private static TrainerTelemetry DecodeGeneral(ReadOnlySpan<byte> p, DateTimeOffset timestamp)
    {
        var speed = (ushort)(p[4] | p[5] << 8);
        int? hr = p[6] == 0xFF ? null : p[6];
        return new(timestamp, 0, 0, speed == 0xFFFF ? 0 : speed * 0.0036, hr,
            HasPowerMeasurement: false, HasCadenceMeasurement: false,
            HasReceivedMeasurement: true, HasSpeedMeasurement: speed != 0xFFFF);
    }

    private static TrainerTelemetry DecodeTrainer(ReadOnlySpan<byte> p, DateTimeOffset timestamp,
        AntFeCPowerAccumulator accumulator)
    {
        int? cadence = p[2] == 0xFF ? null : p[2];
        var instantPower = (ushort)(p[5] | (p[6] & 0x0F) << 8);
        var accumulated = (ushort)(p[3] | p[4] << 8);
        var power = accumulator.Observe(p[1], accumulated, instantPower);
        return new(timestamp, power ?? 0, cadence ?? 0, 0, null,
            HasPowerMeasurement: power.HasValue, HasCadenceMeasurement: cadence.HasValue,
            HasReceivedMeasurement: true, HasSpeedMeasurement: false);
    }
}

/// <summary>Calculates interval power from rolling FE-C event and accumulated-power counters.</summary>
public sealed class AntFeCPowerAccumulator
{
    private byte? _event;
    private ushort? _power;

    public int? Observe(byte eventCount, ushort accumulatedPower, ushort instantaneousPower)
    {
        if (instantaneousPower == 0x0FFF)
        {
            // The profile defines this as invalid for both instantaneous and accumulated power.
            Reset();
            return null;
        }
        int? result = instantaneousPower;
        if (_event.HasValue && _power.HasValue)
        {
            var events = (eventCount - _event.Value + 256) & 0xFF;
            if (events > 0)
            {
                var delta = (accumulatedPower - _power.Value + 65536) & 0xFFFF;
                result = (int)Math.Round((double)delta / events);
            }
        }
        _event = eventCount;
        _power = accumulatedPower;
        return result;
    }

    public void Reset() { _event = null; _power = null; }
}

public readonly record struct AntFeCCommandStatus(byte CommandId, byte Sequence, byte Status)
{
    public bool IsSuccessful => Status == 0;
    public static AntFeCCommandStatus? TryParse(ReadOnlySpan<byte> page) =>
        page.Length == 8 && page[0] == AntFeCProtocol.PageCommandStatus
            ? new(page[1], page[2], page[3]) : null;
}
