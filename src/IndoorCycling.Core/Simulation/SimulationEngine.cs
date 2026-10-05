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
        var gradientRadians = Math.Atan(input.GradientPercent / 100.0);
        var gravityForceNewtons = massKg * _physics.GravityMps2 * Math.Sin(gradientRadians);
        var rollingForceNewtons = massKg * _physics.GravityMps2
            * PositiveOrZero(input.BikeProfile.RollingResistanceCoefficient)
            * Math.Cos(gradientRadians);
        var aeroForceNewtons = 0.5 * _physics.AirDensityKgPerCubicMeter
            * PositiveOrZero(input.BikeProfile.DragCoefficientArea)
            * oldSpeedMps * oldSpeedMps;
        var driveForceNewtons = Math.Max(0, telemetry.PowerWatts) * _physics.DrivetrainEfficiency
            / Math.Max(oldSpeedMps, _physics.PowerForceReferenceSpeedMps);
        var netForceNewtons = driveForceNewtons - gravityForceNewtons
            - rollingForceNewtons - aeroForceNewtons;
        var accelerationMps2 = netForceNewtons / massKg;
        var unboundedNewSpeedMps = oldSpeedMps + accelerationMps2 * deltaSeconds;
        var newSpeedMps = Math.Max(0, unboundedNewSpeedMps);

        // Semi-implicit speed integration with trapezoidal distance keeps game progress
        // consistent with acceleration without consulting trainer-reported speed.
        var deltaDistanceMeters = unboundedNewSpeedMps < 0 && accelerationMps2 < 0
            ? oldSpeedMps * oldSpeedMps / (2 * -accelerationMps2)
            : (oldSpeedMps + newSpeedMps) / 2.0 * deltaSeconds;
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
