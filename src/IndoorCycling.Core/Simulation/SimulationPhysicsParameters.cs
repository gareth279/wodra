namespace IndoorCycling.Core.Simulation;

/// <summary>Environmental and drivetrain constants for the simplified road-load model.</summary>
/// <param name="PowerForceReferenceSpeedMps">Regularizes power-derived drive force below this speed; defaults to 1 m/s for launch response.</param>
public sealed record SimulationPhysicsParameters(
    double GravityMps2 = 9.80665,
    double AirDensityKgPerCubicMeter = 1.225,
    double DrivetrainEfficiency = 0.97,
    double PowerForceReferenceSpeedMps = 1.0)
{
    public static SimulationPhysicsParameters Default { get; } = new();
}
