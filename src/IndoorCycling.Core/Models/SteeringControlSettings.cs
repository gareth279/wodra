namespace IndoorCycling.Core.Models;

/// <summary>Persisted tuning for optional route-relative steering controls.</summary>
public sealed class SteeringControlSettings
{
    public double Sensitivity { get; set; } = 1.0;
    public double DeadZoneDegrees { get; set; } = 3.0;

    public void Normalize()
    {
        Sensitivity = double.IsFinite(Sensitivity) ? Math.Clamp(Sensitivity, 0.25, 2.0) : 1.0;
        DeadZoneDegrees = double.IsFinite(DeadZoneDegrees) ? Math.Clamp(DeadZoneDegrees, 0, 12) : 3.0;
    }
}
