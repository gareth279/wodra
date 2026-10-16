namespace IndoorCycling.Trainer.Models;

/// <summary>Parses the Bluetooth SIG Heart Rate Measurement (0x2A37) value.</summary>
public static class HeartRateMeasurementParser
{
    public static int? Parse(ReadOnlySpan<byte> value)
    {
        if (value.Length < 2) return null;
        var flags = value[0];
        var bpm = (flags & 0x01) != 0
            ? value.Length >= 3 ? value[1] | value[2] << 8 : -1
            : value[1];
        return bpm >= 1 ? bpm : null;
    }
}
