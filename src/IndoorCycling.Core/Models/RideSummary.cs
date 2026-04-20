namespace IndoorCycling.Core.Models;

public sealed record RideSummary(
    string RouteName,
    double DurationSeconds,
    double DistanceMeters,
    double ElevationGainMeters,
    int AveragePowerWatts,
    int AverageCadenceRpm
);