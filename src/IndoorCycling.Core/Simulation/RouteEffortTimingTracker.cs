using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Simulation;

public sealed record ActiveRouteEffortAttempt(RouteEffortDefinition Definition, int AttemptIndex,
    double StartRouteDistanceMeters, double StartElapsedSeconds, double CurrentElapsedSeconds,
    bool Eligible, string? InvalidReason)
{
    public double ElapsedSeconds => Math.Max(0, CurrentElapsedSeconds - StartElapsedSeconds);
}

/// <summary>
/// Interpolates authored route-effort boundaries from authoritative, unwrapped ride chainage.
/// Direction is explicit because overlapping out-and-back road geometry can share the same location.
/// </summary>
public sealed class RouteEffortTimingTracker
{
    private sealed class Attempt
    {
        public required RouteEffortDefinition Definition { get; init; }
        public required int AttemptIndex { get; init; }
        public required double StartDistance { get; init; }
        public required double StartElapsed { get; init; }
        public bool Eligible { get; set; }
        public string? InvalidReason { get; set; }
    }

    private readonly double _routeLength;
    private readonly string _direction;
    private readonly RouteEffortDefinition[] _definitions;
    private readonly Dictionary<string, Attempt> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _nextSegmentStart = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _nextRouteBoundary = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _attemptIndexes = new(StringComparer.Ordinal);
    private readonly List<RouteEffortTimingResult> _completed = [];
    private double _previousDistance;
    private double _previousElapsed;

    public IReadOnlyList<RouteEffortTimingResult> CompletedAttempts => _completed;
    public IReadOnlyList<ActiveRouteEffortAttempt> ActiveAttempts => _active.Values.Select(attempt =>
        new ActiveRouteEffortAttempt(attempt.Definition, attempt.AttemptIndex, attempt.StartDistance,
            attempt.StartElapsed, _previousElapsed, attempt.Eligible, attempt.InvalidReason)).ToArray();
    public string Direction => _direction;

    public RouteEffortTimingTracker(double routeLengthMeters, string direction,
        IEnumerable<RouteEffortDefinition> definitions, double initialDistanceMeters = 0,
        double initialElapsedSeconds = 0)
    {
        if (!double.IsFinite(routeLengthMeters) || routeLengthMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(routeLengthMeters));
        if (!double.IsFinite(initialDistanceMeters) || initialDistanceMeters < 0 ||
            !double.IsFinite(initialElapsedSeconds) || initialElapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(initialDistanceMeters));

        _routeLength = routeLengthMeters;
        _direction = NormalizeDirection(direction);
        _previousDistance = initialDistanceMeters;
        _previousElapsed = initialElapsedSeconds;
        _definitions = definitions.Where(definition =>
        {
            ValidateDefinition(definition, routeLengthMeters);
            return NormalizeDirection(definition.Direction) == _direction;
        }).ToArray();

        foreach (var definition in _definitions)
        {
            _attemptIndexes[definition.Id] = 0;
            if (definition.Kind == RouteEffortKind.FullRoute)
            {
                var next = (Math.Floor(initialDistanceMeters / _routeLength) + 1) * _routeLength;
                _nextRouteBoundary[definition.Id] = next;
                if (Near(initialDistanceMeters, 0))
                    _active[definition.Id] = NewAttempt(definition, NextIndex(definition.Id), 0, initialElapsedSeconds, true, null);
            }
            else
                _nextSegmentStart[definition.Id] = NextSegmentStart(initialDistanceMeters, definition.StartMeters);
        }
    }

    public IReadOnlyList<RouteEffortTimingResult> Advance(double distanceMeters, double elapsedSeconds,
        bool paused = false, bool movementEligible = true, string? ineligibilityReason = null)
    {
        var firstNew = _completed.Count;
        if (!double.IsFinite(distanceMeters) || distanceMeters < 0 || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            return Array.Empty<RouteEffortTimingResult>();

        if (paused)
        {
            InvalidateActive("pause occurred during this effort");
            _previousDistance = distanceMeters;
            _previousElapsed = elapsedSeconds;
            return Array.Empty<RouteEffortTimingResult>();
        }
        if (distanceMeters < _previousDistance - 1e-6 || elapsedSeconds < _previousElapsed - 1e-6)
        {
            MarkDiscontinuity(distanceMeters, elapsedSeconds);
            return Array.Empty<RouteEffortTimingResult>();
        }

        if (!movementEligible) InvalidateActive(ineligibilityReason ?? "ride data is not continuous");
        var beforeDistance = _previousDistance;
        var beforeElapsed = _previousElapsed;
        var afterDistance = distanceMeters;
        var afterElapsed = elapsedSeconds;

        foreach (var definition in _definitions)
        {
            if (definition.Kind == RouteEffortKind.FullRoute)
                AdvanceFullRoute(definition, beforeDistance, afterDistance, beforeElapsed, afterElapsed,
                    movementEligible, ineligibilityReason);
            else
                AdvanceSegment(definition, beforeDistance, afterDistance, beforeElapsed, afterElapsed,
                    movementEligible, ineligibilityReason);
        }

        _previousDistance = afterDistance;
        _previousElapsed = afterElapsed;
        return _completed.Skip(firstNew).ToArray();
    }

    /// <summary>Invalidates in-flight attempts without changing rider state or discarding their elapsed time.</summary>
    public void InvalidateActive(string reason)
    {
        foreach (var attempt in _active.Values)
        {
            attempt.Eligible = false;
            attempt.InvalidReason ??= string.IsNullOrWhiteSpace(reason) ? "attempt invalidated" : reason;
        }
    }

    /// <summary>A jump breaks any crossing; the next attempt must start at a later authored boundary.</summary>
    public void MarkDiscontinuity(double destinationDistanceMeters, double elapsedSeconds)
    {
        _active.Clear();
        _previousDistance = double.IsFinite(destinationDistanceMeters) && destinationDistanceMeters >= 0
            ? destinationDistanceMeters : 0;
        _previousElapsed = double.IsFinite(elapsedSeconds) && elapsedSeconds >= 0 ? elapsedSeconds : 0;
        foreach (var definition in _definitions)
        {
            if (definition.Kind == RouteEffortKind.FullRoute)
                _nextRouteBoundary[definition.Id] = (Math.Floor(_previousDistance / _routeLength) + 1) * _routeLength;
            else
                _nextSegmentStart[definition.Id] = NextSegmentStart(_previousDistance, definition.StartMeters);
        }
    }

    private void AdvanceFullRoute(RouteEffortDefinition definition, double beforeDistance, double afterDistance,
        double beforeElapsed, double afterElapsed, bool movementEligible, string? reason)
    {
        var boundary = _nextRouteBoundary[definition.Id];
        while (DistanceBoundaryMath.Crossed(beforeDistance, afterDistance, boundary))
        {
            var atElapsed = DistanceBoundaryMath.InterpolateElapsed(beforeDistance, afterDistance,
                beforeElapsed, afterElapsed, boundary);
            if (_active.Remove(definition.Id, out var existing))
                Complete(existing, boundary, atElapsed);
            var startLap = boundary / _routeLength;
            _active[definition.Id] = NewAttempt(definition, NextIndex(definition.Id), boundary,
                atElapsed, movementEligible, movementEligible ? null : reason);
            boundary = (startLap + 1) * _routeLength;
        }
        _nextRouteBoundary[definition.Id] = boundary;
    }

    private void AdvanceSegment(RouteEffortDefinition definition, double beforeDistance, double afterDistance,
        double beforeElapsed, double afterElapsed, bool movementEligible, string? reason)
    {
        if (_active.TryGetValue(definition.Id, out var active))
        {
            var finish = active.StartDistance + (definition.EndMeters - definition.StartMeters);
            if (DistanceBoundaryMath.Crossed(beforeDistance, afterDistance, finish))
            {
                var endElapsed = DistanceBoundaryMath.InterpolateElapsed(beforeDistance, afterDistance,
                    beforeElapsed, afterElapsed, finish);
                _active.Remove(definition.Id);
                Complete(active, finish, endElapsed);
            }
        }

        var nextStart = _nextSegmentStart[definition.Id];
        while (!_active.ContainsKey(definition.Id) && DistanceBoundaryMath.Crossed(beforeDistance, afterDistance, nextStart))
        {
            var startElapsed = DistanceBoundaryMath.InterpolateElapsed(beforeDistance, afterDistance,
                beforeElapsed, afterElapsed, nextStart);
            _active[definition.Id] = NewAttempt(definition, NextIndex(definition.Id), nextStart,
                startElapsed, movementEligible, movementEligible ? null : reason);
            var finish = nextStart + (definition.EndMeters - definition.StartMeters);
            if (DistanceBoundaryMath.Crossed(beforeDistance, afterDistance, finish))
            {
                var endElapsed = DistanceBoundaryMath.InterpolateElapsed(beforeDistance, afterDistance,
                    beforeElapsed, afterElapsed, finish);
                var completed = _active[definition.Id];
                _active.Remove(definition.Id);
                Complete(completed, finish, endElapsed);
            }
            nextStart += _routeLength;
        }
        _nextSegmentStart[definition.Id] = nextStart;
    }

    private Attempt NewAttempt(RouteEffortDefinition definition, int index, double startDistance,
        double startElapsed, bool movementEligible, string? reason) => new()
    {
        Definition = definition,
        AttemptIndex = index,
        StartDistance = startDistance,
        StartElapsed = startElapsed,
        Eligible = movementEligible,
        InvalidReason = movementEligible ? null : string.IsNullOrWhiteSpace(reason) ? "ride data is not continuous" : reason
    };

    private void Complete(Attempt attempt, double endDistance, double endElapsed)
    {
        if (!double.IsFinite(endElapsed) || endElapsed <= attempt.StartElapsed)
        {
            attempt.Eligible = false;
            attempt.InvalidReason ??= "effort duration was unavailable";
        }
        _completed.Add(new RouteEffortTimingResult(attempt.Definition, attempt.AttemptIndex,
            attempt.StartDistance, endDistance, attempt.StartElapsed, endElapsed, true,
            attempt.Eligible, attempt.InvalidReason));
    }

    private int NextIndex(string id)
    {
        var index = _attemptIndexes[id];
        _attemptIndexes[id] = index + 1;
        return index;
    }

    private double NextSegmentStart(double distance, double startMeters)
    {
        var lap = Math.Floor(distance / _routeLength);
        var candidate = lap * _routeLength + startMeters;
        while (candidate <= distance + 1e-6) candidate += _routeLength;
        return candidate;
    }

    private static void ValidateDefinition(RouteEffortDefinition definition, double routeLength)
    {
        if (string.IsNullOrWhiteSpace(definition.Id) || definition.Version < 1 || string.IsNullOrWhiteSpace(definition.Name) ||
            !double.IsFinite(definition.StartMeters) || !double.IsFinite(definition.EndMeters) ||
            definition.StartMeters < 0 || definition.EndMeters <= definition.StartMeters || definition.EndMeters > routeLength ||
            string.IsNullOrWhiteSpace(definition.Direction) || !Enum.IsDefined(definition.Kind))
            throw new ArgumentException($"Invalid route effort definition '{definition.Id}'.", nameof(definition));
        if (definition.Kind == RouteEffortKind.FullRoute &&
            (Math.Abs(definition.StartMeters) > 1e-6 || Math.Abs(definition.EndMeters - routeLength) > 1e-6))
            throw new ArgumentException("A full-route effort must span the route's reference line exactly.", nameof(definition));
        if (definition.Kind == RouteEffortKind.Segment && definition.EndMeters - definition.StartMeters <= 0)
            throw new ArgumentException("A route segment must have positive length.", nameof(definition));
    }

    private static string NormalizeDirection(string? direction) => direction?.Trim().ToLowerInvariant() switch
    {
        "forward" => "forward",
        "reverse" => "reverse",
        _ => throw new ArgumentException("Route effort direction must be forward or reverse.", nameof(direction))
    };

    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-6;
}
