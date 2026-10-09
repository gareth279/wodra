using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Simulation;

/// <summary>Deterministic saved-ride rider; its clock is supplied by the active ride.</summary>
public sealed class GhostRiderStateSource : IRiderStateSource
{
    public const double LeftLaneOffsetMeters = 1.35;
    private readonly GhostRideTrace _trace;
    private readonly string _routeId;
    public string RouteId => _routeId;
    private RiderState? _state;
    public bool Finished { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public GhostRideSample? Sample { get; private set; }

    public GhostRiderStateSource(GhostRideTrace trace, string routeId) { _trace = trace; _routeId = routeId; }
    public void Update(double activeElapsedSeconds, double routeLengthMeters, bool paused)
    {
        if (!double.IsFinite(routeLengthMeters) || routeLengthMeters <= 0) return;
        ElapsedSeconds = Math.Clamp(double.IsFinite(activeElapsedSeconds) ? activeElapsedSeconds : 0, 0, _trace.DurationSeconds);
        Finished = activeElapsedSeconds >= _trace.DurationSeconds;
        Sample = _trace.SampleAt(ElapsedSeconds, GhostRideTraceLoader.MaximumInterpolationGapSeconds);
        var total = Sample.DistanceMeters;
        _state = new RiderState("ghost", "Ghost", _routeId, total, ((total % routeLengthMeters) + routeLengthMeters) % routeLengthMeters,
            (int)Math.Floor(total / routeLengthMeters) + 1, LeftLaneOffsetMeters,
            Finished || paused ? 0 : Math.Max(0, Sample.SpeedMps ?? 0), Sample.PowerWatts ?? 0,
            Finished || paused ? 0 : Sample.CadenceRpm ?? 0, paused || Finished, ElapsedSeconds);
    }
    public IReadOnlyList<RiderState> GetRiders() => _state is null ? Array.Empty<RiderState>() : new[] { _state };
}
