using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Simulation;

/// <summary>Applies ride-level pause semantics while leaving telemetry acquisition independent.</summary>
public sealed class RidePauseGate
{
    public bool IsPaused { get; private set; }

    public RideState Pause(RideState state)
    {
        IsPaused = true;
        return state with { VirtualSpeedKph = 0 };
    }

    public void Resume() => IsPaused = false;

    public RideState Toggle(RideState state)
    {
        if (IsPaused)
        {
            Resume();
            return state with { VirtualSpeedKph = 0 };
        }
        return Pause(state);
    }

    public RideState Tick(ISimulationEngine engine, RideState state, SimulationTickInput input)
    {
        if (!IsPaused) return engine.Tick(state, input).NewState;
        return state with
        {
            VirtualSpeedKph = 0,
            CurrentPowerWatts = input.Telemetry.PowerWatts,
            CurrentCadenceRpm = input.Telemetry.CadenceRpm
        };
    }
}
