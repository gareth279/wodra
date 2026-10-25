namespace IndoorCycling.Core.Simulation;

/// <summary>Route-content point on an optional trail line; lift is visual and never part of route elevation.</summary>
public sealed record TrailFeaturePathPoint(double RouteDistanceMeters, double LateralMeters, double SurfaceLiftMeters);

public sealed record TrailFeatureDefinition(string Id, string Name, double StartMeters, double TakeoffMeters,
    double LandingMeters, double EndMeters, double RequiredLateralMeters, double HalfWidthMeters,
    double MinimumSpeedKph, double MaximumAirHeightMeters, int ScorePoints,
    IReadOnlyList<TrailFeaturePathPoint> Path);

public sealed record TrailBermDefinition(string Id, double StartMeters, double EndMeters, float Side,
    float WidthMeters, float HeightMeters);
public sealed record TrailRollerDefinition(string Id, double StartMeters, double PeakMeters, double EndMeters,
    double HeightMeters);

public sealed record TrailFeatureAward(string FeatureId, string Name, int Lap, int Points, int TotalScore);
public sealed record TrailFeatureLapSummary(int Lap, int CompletedLines, int Score);
public sealed record TrailFeatureFrame(double SurfaceLiftMeters = 0, double AirHeightMeters = 0,
    float PitchRadians = 0, string? Feedback = null, TrailFeatureAward? Award = null,
    TrailFeatureLapSummary? CompletedLap = null);

/// <summary>
/// Route-relative jump-line traversal. Progress remains the route's reference chainage; this observer
/// only supplies visual lift and optional challenge points. It never modifies rider state or telemetry.
/// </summary>
public sealed class TrailFeatureChallenge(IReadOnlyList<TrailFeatureDefinition> features, double routeLengthMeters,
    IReadOnlyList<TrailRollerDefinition>? rollers = null)
{
    private readonly HashSet<string> _awarded = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (int Lines, int Score)> _lapResults = [];
    private double _previousDistance;
    private double _previousLateral;
    private bool _hasPrevious;
    private (TrailFeatureDefinition Feature, long Lap)? _airborne;
    private int _totalScore;

    public int TotalScore => _totalScore;
    public int CompletedLines => _awarded.Count;
    public IReadOnlyCollection<string> AwardedFeatureKeys => _awarded;

    public TrailFeatureFrame Update(double distanceMeters, double lateralMeters, double speedKph,
        bool paused, bool scoringEnabled)
    {
        if (!double.IsFinite(distanceMeters) || !double.IsFinite(lateralMeters) || !double.IsFinite(speedKph)
            || routeLengthMeters <= 0)
            return new();
        if (paused)
            return CurrentPresentation(distanceMeters, lateralMeters);
        if (!_hasPrevious)
        {
            _previousDistance = distanceMeters;
            _previousLateral = lateralMeters;
            _hasPrevious = true;
            return CurrentPresentation(distanceMeters, lateralMeters);
        }
        // Reposition, restart, or reverse movement is a discontinuity, never a feature crossing.
        if (distanceMeters < _previousDistance || distanceMeters - _previousDistance > 35)
        {
            _previousDistance = distanceMeters;
            _previousLateral = lateralMeters;
            _airborne = null;
            return CurrentPresentation(distanceMeters, lateralMeters);
        }

        TrailFeatureAward? award = null;
        TrailFeatureLapSummary? lapSummary = null;
        string? feedback = null;
        var firstLap = (long)Math.Floor(_previousDistance / routeLengthMeters);
        var lastLap = (long)Math.Floor(distanceMeters / routeLengthMeters);
        if (lastLap - firstLap <= 1)
        {
            foreach (var feature in features)
            for (var lap = firstLap; lap <= lastLap; lap++)
            {
                var takeoff = lap * routeLengthMeters + feature.TakeoffMeters;
                if (takeoff < _previousDistance || takeoff > distanceMeters) continue;
                var ratio = distanceMeters > _previousDistance
                    ? (takeoff - _previousDistance) / (distanceMeters - _previousDistance) : 0;
                var crossingLateral = _previousLateral + (lateralMeters - _previousLateral) * ratio;
                var expectedLateral = LateralAt(feature, feature.TakeoffMeters);
                if (Math.Abs(crossingLateral - expectedLateral) > feature.HalfWidthMeters
                    || speedKph < feature.MinimumSpeedKph) continue;
                _airborne = (feature, lap);
                feedback = $"{feature.Name} · take-off";
            }

            if (_airborne is { } active)
            {
                var finish = active.Lap * routeLengthMeters + active.Feature.LandingMeters;
                if (_previousDistance < finish && distanceMeters >= finish && scoringEnabled)
                {
                    var key = $"{active.Lap}:{active.Feature.Id}";
                    if (_awarded.Add(key))
                    {
                        _totalScore += active.Feature.ScorePoints;
                        var tally = _lapResults.GetValueOrDefault((int)active.Lap);
                        _lapResults[(int)active.Lap] = (tally.Lines + 1, tally.Score + active.Feature.ScorePoints);
                        award = new(active.Feature.Id, active.Feature.Name, (int)active.Lap,
                            active.Feature.ScorePoints, _totalScore);
                        feedback = $"{active.Feature.Name} +{active.Feature.ScorePoints} · {_totalScore} points";
                    }
                }
                if (distanceMeters > active.Lap * routeLengthMeters + active.Feature.EndMeters)
                    _airborne = null;
            }
            if (lastLap > firstLap)
            {
                var completed = _lapResults.GetValueOrDefault((int)firstLap);
                lapSummary = new((int)firstLap + 1, completed.Lines, completed.Score);
            }
        }

        _previousDistance = distanceMeters;
        _previousLateral = lateralMeters;
        return CurrentPresentation(distanceMeters, lateralMeters) with
        { Feedback = feedback, Award = award, CompletedLap = lapSummary };
    }

    public void SuppressNextSweep(double distanceMeters, double lateralMeters)
    {
        _previousDistance = distanceMeters;
        _previousLateral = lateralMeters;
        _hasPrevious = true;
        _airborne = null;
    }

    public static double LateralAt(TrailFeatureDefinition feature, double distanceMeters) =>
        Interpolate(feature, distanceMeters).LateralMeters;

    public static double SurfaceLiftAt(TrailFeatureDefinition feature, double distanceMeters) =>
        Interpolate(feature, distanceMeters).SurfaceLiftMeters;

    /// <summary>Maximum route-relative lateral extent of the continuous bypass/branch corridor.</summary>
    public double LateralCorridorHalfWidth(double rideDistanceMeters, double mainLineHalfWidthMeters)
    {
        if (!double.IsFinite(rideDistanceMeters) || !double.IsFinite(mainLineHalfWidthMeters)
            || mainLineHalfWidthMeters <= 0 || routeLengthMeters <= 0) return 0;
        var wrapped = ((rideDistanceMeters % routeLengthMeters) + routeLengthMeters) % routeLengthMeters;
        var extent = mainLineHalfWidthMeters;
        foreach (var feature in features)
        {
            if (wrapped < feature.StartMeters || wrapped > feature.EndMeters) continue;
            extent = Math.Max(extent, Math.Abs(LateralAt(feature, wrapped)) + mainLineHalfWidthMeters);
        }
        return extent;
    }

    private TrailFeatureFrame CurrentPresentation(double distanceMeters, double lateralMeters)
    {
        var wrapped = ((distanceMeters % routeLengthMeters) + routeLengthMeters) % routeLengthMeters;
        var rollerLift = Math.Abs(lateralMeters) <= 0.9 ? RollerLiftAt(wrapped) : 0;
        foreach (var feature in features)
        {
            if (wrapped < feature.StartMeters || wrapped > feature.EndMeters) continue;
            var point = Interpolate(feature, wrapped);
            if (Math.Abs(lateralMeters - point.LateralMeters) > feature.HalfWidthMeters) continue;
            var lift = point.SurfaceLiftMeters;
            if (_airborne is { } active && active.Feature.Id == feature.Id)
            {
                var absoluteTakeoff = active.Lap * routeLengthMeters + feature.TakeoffMeters;
                var absoluteLanding = active.Lap * routeLengthMeters + feature.LandingMeters;
                var progress = Math.Clamp((distanceMeters - absoluteTakeoff) / (absoluteLanding - absoluteTakeoff), 0, 1);
                var air = Math.Sin(Math.PI * progress) * feature.MaximumAirHeightMeters;
                var pitch = (float)(Math.Cos(Math.PI * progress) * 0.13);
                return new(lift + rollerLift, air, pitch);
            }
            return new(lift + rollerLift);
        }
        return new(rollerLift);
    }

    public double RollerLiftAt(double distanceMeters)
    {
        foreach (var roller in rollers ?? [])
        {
            if (distanceMeters < roller.StartMeters || distanceMeters > roller.EndMeters) continue;
            var t = (distanceMeters - roller.StartMeters) / (roller.EndMeters - roller.StartMeters);
            var peak = (roller.PeakMeters - roller.StartMeters) / (roller.EndMeters - roller.StartMeters);
            var lift = t <= peak
                ? Math.Sin(Math.PI * .5 * Math.Clamp(t / peak, 0, 1))
                : Math.Cos(Math.PI * .5 * Math.Clamp((t - peak) / (1 - peak), 0, 1));
            return lift * roller.HeightMeters;
        }
        return 0;
    }

    private static TrailFeaturePathPoint Interpolate(TrailFeatureDefinition feature, double distance)
    {
        var points = feature.Path;
        var low = 0;
        var high = points.Count - 1;
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (points[middle].RouteDistanceMeters <= distance) low = middle;
            else high = middle;
        }
        var a = points[low];
        var b = points[high];
        var t = b.RouteDistanceMeters > a.RouteDistanceMeters
            ? Math.Clamp((distance - a.RouteDistanceMeters) / (b.RouteDistanceMeters - a.RouteDistanceMeters), 0, 1) : 0;
        return new(distance, a.LateralMeters + (b.LateralMeters - a.LateralMeters) * t,
            a.SurfaceLiftMeters + (b.SurfaceLiftMeters - a.SurfaceLiftMeters) * t);
    }
}
