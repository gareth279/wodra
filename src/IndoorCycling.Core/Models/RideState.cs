namespace IndoorCycling.Core.Models;

public sealed record RideState(
    double ElapsedSeconds,
    double DistanceMeters,
    double ElevationGainMeters,
    double CurrentGradientPercent,
    double VirtualSpeedKph,
    int CurrentPowerWatts,
    int CurrentCadenceRpm
)
{
    public static RideState Initial =>
        new(
            ElapsedSeconds: 0,
            DistanceMeters: 0,
            ElevationGainMeters: 0,
            CurrentGradientPercent: 0,
            VirtualSpeedKph: 0,
            CurrentPowerWatts: 0,
            CurrentCadenceRpm: 0
        );
}