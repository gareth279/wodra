using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Simulation;

public interface ISimulationEngine
{
    SimulationTickResult Tick(RideState currentState, SimulationTickInput input);
}