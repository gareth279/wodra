namespace IndoorCycling.Core.Models;

public sealed class ReplayFrame
{
    public TimeSpan Elapsed { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    public double? SpeedMps { get; init; }
    public double? DistanceMeters { get; init; }
    public int? PowerWatts { get; init; }
    public int? CadenceRpm { get; init; }
    public int? HeartRateBpm { get; init; }

    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? ElevationMeters { get; init; }
}