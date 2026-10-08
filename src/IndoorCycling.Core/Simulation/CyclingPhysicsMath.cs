namespace IndoorCycling.Core.Simulation;

public readonly record struct CyclingPhysicsStep(double SpeedMetersPerSecond,
    double DistanceMeters, double AccelerationMetersPerSecondSquared);

/// <summary>Pure road-load integration shared by player simulation and local AI riders.</summary>
public static class CyclingPhysicsMath
{
    public static CyclingPhysicsStep Advance(double oldSpeedMetersPerSecond, double powerWatts,
        double gradientPercent, double totalMassKg, double rollingResistanceCoefficient,
        double dragCoefficientArea, double deltaSeconds,
        SimulationPhysicsParameters? parameters = null)
    {
        var physics = parameters ?? SimulationPhysicsParameters.Default;
        var delta = Math.Max(0, deltaSeconds);
        var speed = Math.Max(0, double.IsFinite(oldSpeedMetersPerSecond) ? oldSpeedMetersPerSecond : 0);
        var mass = Math.Max(1, PositiveOrZero(totalMassKg));
        var gradientRadians = Math.Atan(gradientPercent / 100.0);
        var gravityForceNewtons = mass * physics.GravityMps2 * Math.Sin(gradientRadians);
        var rollingForceNewtons = mass * physics.GravityMps2
            * PositiveOrZero(rollingResistanceCoefficient) * Math.Cos(gradientRadians);
        var aeroForceNewtons = 0.5 * physics.AirDensityKgPerCubicMeter
            * PositiveOrZero(dragCoefficientArea) * speed * speed;
        var driveForceNewtons = Math.Max(0, powerWatts) * physics.DrivetrainEfficiency
            / Math.Max(speed, physics.PowerForceReferenceSpeedMps);
        var acceleration = (driveForceNewtons - gravityForceNewtons - rollingForceNewtons - aeroForceNewtons) / mass;
        var unboundedSpeed = speed + acceleration * delta;
        var nextSpeed = Math.Max(0, unboundedSpeed);
        var distance = unboundedSpeed < 0 && acceleration < 0
            ? speed * speed / (2 * -acceleration)
            : (speed + nextSpeed) / 2.0 * delta;
        return new CyclingPhysicsStep(nextSpeed, distance, acceleration);
    }

    private static double PositiveOrZero(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
