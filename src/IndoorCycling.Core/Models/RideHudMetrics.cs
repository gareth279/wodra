namespace IndoorCycling.Core.Models;

/// <summary>Display values projected from the authoritative ride state and sensor freshness.</summary>
public readonly record struct RideHudMetrics(
    int? PowerWatts,
    int? CadenceRpm,
    int? HeartRateBpm,
    double SimulatedSpeedKph,
    double GradientPercent,
    double DistanceMeters,
    double ElapsedSeconds)
{
    public static RideHudMetrics From(RideState state, TrainerTelemetry? telemetry,
        DateTimeOffset now, double elapsedSeconds, TimeSpan? sensorMaxAge = null)
    {
		var sensorIsFresh = telemetry?.IsFreshAt(now, sensorMaxAge) == true;
		var heartRateTime = telemetry?.HeartRateTimestamp ?? telemetry?.Timestamp ?? DateTimeOffset.MinValue;
		var heartRateFresh = telemetry?.HeartRateBpm.HasValue == true && heartRateTime <= now && now - heartRateTime <= TimeSpan.FromSeconds(8);

        return new RideHudMetrics(
            sensorIsFresh ? state.CurrentPowerWatts : null,
            sensorIsFresh ? state.CurrentCadenceRpm : null,
            heartRateFresh ? telemetry!.HeartRateBpm : null,
            state.VirtualSpeedKph,
            state.CurrentGradientPercent,
            state.DistanceMeters,
            elapsedSeconds);
    }
}
