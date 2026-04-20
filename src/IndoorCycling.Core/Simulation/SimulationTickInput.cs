using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Simulation;

public sealed record SimulationTickInput(
    TrainerTelemetry Telemetry,
    double GradientPercent,
    double DeltaSeconds,
    RiderProfile RiderProfile,
    BikeProfile BikeProfile
);