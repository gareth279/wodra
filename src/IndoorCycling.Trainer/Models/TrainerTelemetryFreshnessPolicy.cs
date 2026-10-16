using IndoorCycling.Core.Models;

namespace IndoorCycling.Trainer.Models;

/// <summary>Applies the same freshness rule to UI state, ride input and connection-loss detection.</summary>
public static class TrainerTelemetryFreshnessPolicy
{
    public static bool HasExpired(DateTimeOffset lastFreshTelemetry, DateTimeOffset now, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (lastFreshTelemetry == DateTimeOffset.MinValue) return true;
        var age = now - lastFreshTelemetry;
        return age < TimeSpan.Zero || age >= timeout;
    }

    public static bool IsFresh(TrainerTelemetry telemetry, DateTimeOffset now, TimeSpan timeout)
        => telemetry.HasReceivedMeasurement && telemetry.IsFreshAt(now, timeout);
}
