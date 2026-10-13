using IndoorCycling.Core.Models;

namespace IndoorCycling.Trainer.Models;

public enum TelemetryReadingState { Awaiting, Unsupported, Stale, Fresh }

public readonly record struct TelemetryReading(int? Value, TelemetryReadingState State);

public static class TrainerTelemetryPresentation
{
    public static TelemetryReading Power(TrainerTelemetry telemetry, DateTimeOffset now, TimeSpan? maxAge = null)
        => Get(telemetry, telemetry.PowerWatts, telemetry.HasPowerMeasurement, now, maxAge);

    public static TelemetryReading Cadence(TrainerTelemetry telemetry, DateTimeOffset now, TimeSpan? maxAge = null)
        => Get(telemetry, telemetry.CadenceRpm, telemetry.HasCadenceMeasurement, now, maxAge);

    private static TelemetryReading Get(TrainerTelemetry telemetry, int value, bool supported, DateTimeOffset now, TimeSpan? maxAge)
    {
        if (!telemetry.HasReceivedMeasurement) return new(null, TelemetryReadingState.Awaiting);
        if (!telemetry.IsFreshAt(now, maxAge)) return new(null, TelemetryReadingState.Stale);
        return supported ? new(value, TelemetryReadingState.Fresh) : new(null, TelemetryReadingState.Unsupported);
    }
}
