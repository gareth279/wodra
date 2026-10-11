namespace IndoorCycling.Core.Models;

/// <summary>Display values projected from the authoritative ride state and sensor freshness.</summary>
public readonly record struct RideHudMetrics(
    int? PowerWatts,
    int? CadenceRpm,
    double SimulatedSpeedKph,
    double GradientPercent,
    double DistanceMeters,
    double ElapsedSeconds)
{
    public static RideHudMetrics From(RideState state, TrainerTelemetry? telemetry,
        DateTimeOffset now, double elapsedSeconds, TimeSpan? sensorMaxAge = null)
    {
		var sensorIsFresh = telemetry?.IsFreshAt(now, sensorMaxAge) == true;

        return new RideHudMetrics(
            sensorIsFresh ? state.CurrentPowerWatts : null,
            sensorIsFresh ? state.CurrentCadenceRpm : null,
            state.VirtualSpeedKph,
            state.CurrentGradientPercent,
            state.DistanceMeters,
            elapsedSeconds);
    }
}
