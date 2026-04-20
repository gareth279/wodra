namespace IndoorCycling.Core.Models;

public sealed record RideSample(
    DateTimeOffset Timestamp,
    double ElapsedSeconds,
    double DistanceMeters,
    double GradientPercent,
    double VirtualSpeedKph,
    int PowerWatts,
    int CadenceRpm
);