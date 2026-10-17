namespace IndoorCycling.Core.Models;

/// <summary>Persisted tuning for optional route-relative steering controls.</summary>
public sealed class SteeringControlSettings
{
    public const double MinimumSensitivity = 0.5;
    public const double MaximumSensitivity = 3.0;
    public const double DefaultSensitivity = 1.5;

    /// <summary>Shared multiplier for keyboard and Sterzo steering, persisted as Sensitivity for existing profiles.</summary>
    public double Sensitivity { get; set; } = DefaultSensitivity;
    public double DeadZoneDegrees { get; set; } = 3.0;

    public void Normalize()
    {
        Sensitivity = double.IsFinite(Sensitivity)
            ? Math.Clamp(Sensitivity, MinimumSensitivity, MaximumSensitivity)
            : DefaultSensitivity;
        DeadZoneDegrees = double.IsFinite(DeadZoneDegrees) ? Math.Clamp(DeadZoneDegrees, 0, 12) : 3.0;
    }
}
