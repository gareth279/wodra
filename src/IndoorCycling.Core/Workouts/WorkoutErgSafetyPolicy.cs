using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Workouts;

/// <summary>Only a fresh, measured zero-cadence sample releases ERG; missing/stale cadence never changes targets.</summary>
public static class WorkoutErgSafetyPolicy
{
    public static bool ShouldReleaseOnZeroCadence(bool workoutActive, bool ergRequested, bool controlActive,
        TrainerTelemetry telemetry, DateTimeOffset now) =>
        workoutActive && ergRequested && controlActive && telemetry.IsFreshAt(now) &&
        telemetry.HasCadenceMeasurement && telemetry.CadenceRpm == 0;
}
