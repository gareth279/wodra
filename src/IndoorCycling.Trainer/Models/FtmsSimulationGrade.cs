using System.Buffers.Binary;

namespace IndoorCycling.Trainer.Models;

/// <summary>Converts grade percentages to the signed 0.01% FTMS simulation field.</summary>
public static class FtmsSimulationGrade
{
    public const double MinimumRepresentablePercent = short.MinValue / 100.0;
    public const double MaximumRepresentablePercent = short.MaxValue / 100.0;

    public static short Encode(double requestedPercent, (double Minimum, double Maximum)? supportedRangePercent = null)
    {
        var finite = double.IsFinite(requestedPercent) ? requestedPercent : 0;
        if (supportedRangePercent is { } range
            && double.IsFinite(range.Minimum) && double.IsFinite(range.Maximum) && range.Minimum <= range.Maximum)
            finite = Math.Clamp(finite, range.Minimum, range.Maximum);
        finite = Math.Clamp(finite, MinimumRepresentablePercent, MaximumRepresentablePercent);
        return (short)Math.Round(finite * 100.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Decodes FTMS Supported Inclination Range (signed tenths of a percent).</summary>
    public static bool TryDecodeSupportedInclinationRange(ReadOnlySpan<byte> value,
        out (double Minimum, double Maximum) rangePercent)
    {
        rangePercent = default;
        if (value.Length < 6) return false;
        var minimum = BinaryPrimitives.ReadInt16LittleEndian(value) / 10.0;
        var maximum = BinaryPrimitives.ReadInt16LittleEndian(value[2..]) / 10.0;
        if (minimum > maximum) return false;
        rangePercent = (minimum, maximum);
        return true;
    }
}
