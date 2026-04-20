namespace IndoorCycling.Core.Models;

public sealed record TrainerTelemetry(
    DateTimeOffset Timestamp,
    int PowerWatts,
    int CadenceRpm,
    double SpeedKph,
    int? HeartRateBpm = null
);