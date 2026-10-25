namespace IndoorCycling.Trainer.Models;

/// <summary>Parses FTMS Fitness Machine Feature (0x2ACC) and Supported Power Range (0x2AD8).</summary>
public sealed record FtmsWorkoutCapabilities(bool HasFeatureData, bool SupportsTargetPower,
    bool SupportsSimulation, int MinimumPowerWatts, int MaximumPowerWatts, int PowerIncrementWatts)
{
    public static FtmsWorkoutCapabilities Unknown { get; } = new(false, false, false, 0, 1000, 1);

    public static FtmsWorkoutCapabilities Parse(ReadOnlySpan<byte> feature, ReadOnlySpan<byte> powerRange)
    {
        if (feature.Length < 8) return Unknown;
        var targetFeatures = (uint)(feature[4] | feature[5] << 8 | feature[6] << 16 | feature[7] << 24);
        var supportsPower = (targetFeatures & (1u << 3)) != 0;
        var supportsSimulation = (targetFeatures & (1u << 13)) != 0;
        if (powerRange.Length < 6) return new(true, supportsPower, supportsSimulation, 0, 1000, 1);
        var min = (short)(powerRange[0] | powerRange[1] << 8);
        var max = (short)(powerRange[2] | powerRange[3] << 8);
        var increment = (ushort)(powerRange[4] | powerRange[5] << 8);
        if (min < 0 || max <= min || max > 4000 || increment == 0 || increment > max)
            return new(true, supportsPower, supportsSimulation, 0, 1000, 1);
        return new(true, supportsPower, supportsSimulation, min, max, increment);
    }
}
