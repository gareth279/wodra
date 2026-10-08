using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Simulation;

public sealed class SimulationEngine : ISimulationEngine
{
    private readonly SimulationPhysicsParameters _physics;

    public SimulationEngine(SimulationPhysicsParameters? physics = null)
    {
        _physics = physics ?? SimulationPhysicsParameters.Default;
    }

    public SimulationTickResult Tick(RideState currentState, SimulationTickInput input)
    {
        var telemetry = input.Telemetry;
        var deltaSeconds = Math.Max(0, input.DeltaSeconds);
        var oldSpeedMps = Math.Max(0, currentState.VirtualSpeedKph / 3.6);
        var massKg = Math.Max(1, PositiveOrZero(input.RiderProfile.WeightKg)
            + PositiveOrZero(input.BikeProfile.WeightKg));
        var step = CyclingPhysicsMath.Advance(oldSpeedMps, telemetry.PowerWatts, input.GradientPercent,
            massKg, input.BikeProfile.RollingResistanceCoefficient, input.BikeProfile.DragCoefficientArea,
            deltaSeconds, _physics);
        var newSpeedMps = step.SpeedMetersPerSecond;
        var deltaDistanceMeters = step.DistanceMeters;
        var speedKph = newSpeedMps * 3.6;

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

    private static double PositiveOrZero(double value) =>
        double.IsFinite(value) ? Math.Max(0, value) : 0;
}
