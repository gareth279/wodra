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
    private readonly bool _repeatLap;
    private readonly bool _timeComparisonAvailable;
    private readonly string _comparisonLabel;
    private readonly IReadOnlyDictionary<string, double> _segmentTimes;
    private double? _lastPlayerDistance;
    private double _lastPlayerElapsed;
    private double _playerLapStartElapsed;
    private long _playerLapIndex;
    public bool Finished { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public GhostRideSample? Sample { get; private set; }
    public bool TimeComparisonAvailable => _timeComparisonAvailable;
    public string ComparisonLabel => _comparisonLabel;
    public bool RepeatsRouteLap => _repeatLap;

    public GhostRiderStateSource(GhostRideTrace trace, string routeId, string comparisonLabel = "Saved ride",
        bool timeComparisonAvailable = false, bool repeatRouteLap = false,
        IReadOnlyDictionary<string, double>? segmentTimes = null)
    {
        _trace = trace;
        _routeId = routeId;
        _comparisonLabel = string.IsNullOrWhiteSpace(comparisonLabel) ? "Saved ride" : comparisonLabel;
        _timeComparisonAvailable = timeComparisonAvailable;
        _repeatLap = repeatRouteLap;
        _segmentTimes = segmentTimes ?? new Dictionary<string, double>(StringComparer.Ordinal);
    }

    public void Update(double activeElapsedSeconds, double routeLengthMeters, bool paused, double playerDistanceMeters = 0)
    {
        if (!double.IsFinite(routeLengthMeters) || routeLengthMeters <= 0) return;
        var rideElapsed = Math.Max(0, double.IsFinite(activeElapsedSeconds) ? activeElapsedSeconds : 0);
        var distanceOffset = 0d;
        if (_repeatLap && double.IsFinite(playerDistanceMeters) && playerDistanceMeters >= 0)
        {
            if (_lastPlayerDistance is null)
            {
                _playerLapIndex = (long)Math.Floor(playerDistanceMeters / routeLengthMeters);
                _playerLapStartElapsed = rideElapsed;
            }
            else
            {
                var before = _lastPlayerDistance.Value;
                var boundaryIndex = (long)Math.Floor(before / routeLengthMeters) + 1;
                while (DistanceBoundaryMath.Crossed(before, playerDistanceMeters, boundaryIndex * routeLengthMeters))
                {
                    _playerLapStartElapsed = DistanceBoundaryMath.InterpolateElapsed(before, playerDistanceMeters,
                        _lastPlayerElapsed, rideElapsed, boundaryIndex * routeLengthMeters);
                    _playerLapIndex = boundaryIndex;
                    boundaryIndex++;
                }
            }
            _lastPlayerDistance = playerDistanceMeters;
            _lastPlayerElapsed = rideElapsed;
            ElapsedSeconds = Math.Clamp(rideElapsed - _playerLapStartElapsed, 0, _trace.DurationSeconds);
            distanceOffset = _playerLapIndex * routeLengthMeters;
        }
        else
            ElapsedSeconds = Math.Clamp(rideElapsed, 0, _trace.DurationSeconds);

        Finished = _repeatLap
            ? rideElapsed - _playerLapStartElapsed >= _trace.DurationSeconds
            : rideElapsed >= _trace.DurationSeconds;
        Sample = _trace.SampleAt(ElapsedSeconds, GhostRideTraceLoader.MaximumInterpolationGapSeconds);
        var total = distanceOffset + Sample.DistanceMeters;
        _state = new RiderState("ghost", $"Ghost · {_comparisonLabel}", _routeId, total, ((total % routeLengthMeters) + routeLengthMeters) % routeLengthMeters,
            (int)Math.Floor(total / routeLengthMeters) + 1, LeftLaneOffsetMeters,
            Finished || paused ? 0 : Math.Max(0, Sample.SpeedMps ?? 0), Sample.PowerWatts ?? 0,
            Finished || paused ? 0 : Sample.CadenceRpm ?? 0, paused || Finished, ElapsedSeconds);
    }

    /// <summary>Positive means the local rider reached the same route progress sooner than this ghost.</summary>
    public bool TryGetSignedTimeGap(double playerDistanceMeters, double playerElapsedSeconds,
        double routeLengthMeters, out double seconds)
    {
        seconds = 0;
        if (!_timeComparisonAvailable || !double.IsFinite(playerDistanceMeters) || playerDistanceMeters < 0 ||
            !double.IsFinite(playerElapsedSeconds) || playerElapsedSeconds < 0 || routeLengthMeters <= 0)
            return false;
        var progress = _repeatLap ? playerDistanceMeters % routeLengthMeters : playerDistanceMeters;
        if (!_trace.TryGetElapsedAtDistance(progress, out var ghostElapsed,
            GhostRideTraceLoader.MaximumInterpolationGapSeconds)) return false;
        var playerElapsed = _repeatLap ? playerElapsedSeconds - _playerLapStartElapsed : playerElapsedSeconds;
        if (playerElapsed < 0) return false;
        seconds = ghostElapsed - playerElapsed;
        return double.IsFinite(seconds);
    }

    public bool TryGetSegmentTime(string segmentId, out double elapsedSeconds) =>
        _segmentTimes.TryGetValue(segmentId, out elapsedSeconds);

    public IReadOnlyList<RiderState> GetRiders() => _state is null ? Array.Empty<RiderState>() : new[] { _state };
}
