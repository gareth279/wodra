namespace IndoorCycling.Core.Models;

/// <summary>Source-agnostic runtime state for a rider; contains no persistence or engine objects.</summary>
public sealed record RiderState(
    string RiderId,
    string DisplayName,
    string RouteId,
    double RouteDistanceMeters,
    double WrappedRouteDistanceMeters,
    int LapNumber,
    double LateralOffsetMeters,
    double VirtualSpeedMps,
    int PowerWatts,
    int CadenceRpm,
    bool Paused,
    double SimulationTimeSeconds);

/// <summary>Read-only boundary between rider-state producers and the renderer.</summary>
public interface IRiderStateSource
{
    IReadOnlyList<RiderState> GetRiders();
}
