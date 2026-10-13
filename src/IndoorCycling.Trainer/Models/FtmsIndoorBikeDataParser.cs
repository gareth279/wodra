using IndoorCycling.Core.Models;

namespace IndoorCycling.Trainer.Models;

/// <summary>Parses the optional fields in FTMS Indoor Bike Data (UUID 0x2AD2).</summary>
public static class FtmsIndoorBikeDataParser
{
    private const ushort MoreData = 1 << 0;
    private const ushort AverageSpeed = 1 << 1;
    private const ushort InstantCadence = 1 << 2;
    private const ushort AverageCadence = 1 << 3;
    private const ushort TotalDistance = 1 << 4;
    private const ushort ResistanceLevel = 1 << 5;
    private const ushort InstantPower = 1 << 6;
    private const ushort AveragePower = 1 << 7;
    private const ushort ExpendedEnergy = 1 << 8;
    private const ushort HeartRate = 1 << 9;
    private const ushort MetabolicEquivalent = 1 << 10;
    private const ushort ElapsedTime = 1 << 11;
    private const ushort RemainingTime = 1 << 12;

    public static bool TryParse(ReadOnlySpan<byte> data, DateTimeOffset timestamp, out TrainerTelemetry telemetry)
    {
        telemetry = new(timestamp, 0, 0, 0, HasPowerMeasurement: false, HasCadenceMeasurement: false, HasReceivedMeasurement: false, HasSpeedMeasurement: false);
        if (data.Length < 2) return false;
        var flags = (ushort)(data[0] | data[1] << 8);
        var offset = 2;
        var speed = 0d;
        var cadence = 0;
        var power = 0;
        var heartRate = (int?)null;

        if ((flags & MoreData) == 0)
        {
            if (!TrySkip(data, ref offset, 2)) return false;
            speed = (ushort)(data[offset - 2] | data[offset - 1] << 8) / 100d;
        }
        if ((flags & AverageSpeed) != 0 && !TrySkip(data, ref offset, 2)) return false;
        if ((flags & InstantCadence) != 0)
        {
            if (!TrySkip(data, ref offset, 2)) return false;
            cadence = (int)Math.Round((ushort)(data[offset - 2] | data[offset - 1] << 8) / 2d, MidpointRounding.AwayFromZero);
        }
        if ((flags & AverageCadence) != 0 && !TrySkip(data, ref offset, 2)) return false;
        if ((flags & TotalDistance) != 0 && !TrySkip(data, ref offset, 3)) return false;
        if ((flags & ResistanceLevel) != 0 && !TrySkip(data, ref offset, 2)) return false;
        if ((flags & InstantPower) != 0)
        {
            if (!TrySkip(data, ref offset, 2)) return false;
            power = unchecked((short)(data[offset - 2] | data[offset - 1] << 8));
        }
        if ((flags & AveragePower) != 0 && !TrySkip(data, ref offset, 2)) return false;
        if ((flags & ExpendedEnergy) != 0 && !TrySkip(data, ref offset, 5)) return false;
        if ((flags & HeartRate) != 0)
        {
            if (!TrySkip(data, ref offset, 1)) return false;
            heartRate = data[offset - 1];
        }
        if ((flags & MetabolicEquivalent) != 0 && !TrySkip(data, ref offset, 1)) return false;
        if ((flags & ElapsedTime) != 0 && !TrySkip(data, ref offset, 2)) return false;
        if ((flags & RemainingTime) != 0 && !TrySkip(data, ref offset, 2)) return false;

        telemetry = new TrainerTelemetry(timestamp, power, cadence, speed, heartRate,
            HasPowerMeasurement: (flags & InstantPower) != 0,
            HasCadenceMeasurement: (flags & InstantCadence) != 0,
            HasReceivedMeasurement: true,
            HasSpeedMeasurement: (flags & MoreData) == 0);
        return true;
    }

    private static bool TrySkip(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        if (data.Length - offset < count) return false;
        offset += count;
        return true;
    }
}
