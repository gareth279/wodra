using IndoorCycling.Core.Models;

namespace IndoorCycling.Trainer.Models;

public readonly record struct AntFeCDeviceIdentity(ushort DeviceNumber, byte DeviceType, byte TransmissionType)
{
    public string DisplayName => $"ANT+ FE-C trainer {DeviceNumber}";
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
