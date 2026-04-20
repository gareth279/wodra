using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Simulation;

public sealed class SimulationEngine : ISimulationEngine
{
    public SimulationTickResult Tick(RideState currentState, SimulationTickInput input)
    {
        var telemetry = input.Telemetry;

        // Very rough starter model:
        // - use trainer speed if available
        // - if speed looks invalid, estimate from power and gradient
        var speedKph = telemetry.SpeedKph > 0
            ? telemetry.SpeedKph
            : EstimateSpeedKph(telemetry.PowerWatts, input.GradientPercent);

        var deltaDistanceMeters = (speedKph / 3.6) * input.DeltaSeconds;

        var elevationGainMeters = input.GradientPercent > 0
            ? deltaDistanceMeters * (input.GradientPercent / 100.0)
            : 0;

        var newState = currentState with
        {
            ElapsedSeconds = currentState.ElapsedSeconds + input.DeltaSeconds,
            DistanceMeters = currentState.DistanceMeters + deltaDistanceMeters,
            ElevationGainMeters = currentState.ElevationGainMeters + elevationGainMeters,
            CurrentGradientPercent = input.GradientPercent,
            VirtualSpeedKph = speedKph,
            CurrentPowerWatts = telemetry.PowerWatts,
            CurrentCadenceRpm = telemetry.CadenceRpm
        };

        return new SimulationTickResult(newState);
    }

    private static double EstimateSpeedKph(int powerWatts, double gradientPercent)
    {
        var baseSpeed = Math.Max(8.0, powerWatts * 0.12);
        var gradientPenalty = gradientPercent * 1.5;
        return Math.Max(5.0, baseSpeed - gradientPenalty);
    }
}