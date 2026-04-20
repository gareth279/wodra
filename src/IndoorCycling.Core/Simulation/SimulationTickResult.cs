using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Simulation;

public sealed record SimulationTickResult(
    RideState NewState
);