using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

public readonly record struct VisualRouteSample(double DistanceMeters, Vector3 Position, Vector3 Forward);
public readonly record struct VisualRoutePose(
    Vector3 Position, Vector3 Forward, double LoopDistanceMeters,
    int SampleIndexBefore, int SampleIndexAfter, float Interpolation)
{
    public float BankSlope { get; init; }
}

/// <summary>Canonical sampled visual centreline shared by route rendering and rider pose sampling.
/// Coordinates: +X east, +Y up, -Z north. One unit is one metre.
/// No trainer or Godot dependencies; route definitions remain authoritative.</summary>
public sealed class VisualRoute
{
    private const double EarthRadiusMeters = 6371000;
    private readonly VisualRouteSample[] _samples;
    private readonly RoutePoint? _geographicOrigin;
    private readonly RoutePoint? _localOrigin;
    public IReadOnlyList<VisualRouteSample> Samples => _samples;
    // Compatibility alias; this is the same canonical sample array, not a second path.
    public IReadOnlyList<VisualRouteSample> Vertices => _samples;
    public double LengthMeters { get; }
    /// <summary>Measured sampled centreline length before any non-track legacy adjustment.</summary>
    public double CenterlineLengthMeters { get; }
    public bool IsPointToPoint { get; }
    public Vector3 ClosureCorrection { get; }
    public float GroundPlaneY { get; }
    public const float GroundClearanceMeters = 10;

    // Synthetic/nonclosed data is visually closed over the last quarter of a lap.
    // This affects rendering only, never route grades, ride distance or FIT samples.
    public const double ClosureStartFraction = 0.75;

    /// <summary>Maps WGS84 coordinates into this route's local east/up/south world frame.</summary>
    public Vector3 GetPositionForCoordinate(double latitude, double longitude, double elevationMeters)
    {
        var origin = _geographicOrigin
            ?? throw new InvalidOperationException("Geographic landmark positions require a route with GPS points.");
        const double radians = Math.PI / 180;
        return new Vector3(
            (float)((longitude - origin.Longitude!.Value) * radians * EarthRadiusMeters * Math.Cos(origin.Latitude!.Value * radians)),
            (float)(elevationMeters - origin.ElevationMeters),
            (float)(-(latitude - origin.Latitude.Value) * radians * EarthRadiusMeters));
    }

    /// <summary>Maps fictional route-local scenery coordinates without inventing GPS positions.</summary>
    public Vector3 GetPositionForLocalCoordinate(double xMeters, double zMeters, double elevationMeters)
    {
        var origin = _localOrigin ?? throw new InvalidOperationException("Local scenery coordinates require a local-coordinate route.");
        return new Vector3((float)(xMeters - origin.LocalXMeters!.Value),
            (float)(elevationMeters - origin.ElevationMeters), (float)(zMeters - origin.LocalZMeters!.Value));
    }

    public VisualRoute(RouteDefinition route, double spacingMeters = 10)
    {
        if (!double.IsFinite(route.DistanceMeters) || route.DistanceMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(route), "Visual routes require a positive length.");
        if (!double.IsFinite(spacingMeters) || spacingMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(spacingMeters));
        _trackBanked = route.Environment == RouteEnvironment.Velodrome;
        IsPointToPoint = route.RouteType == "Point-to-Point";
        LengthMeters = route.DistanceMeters;
        var distances = new SortedSet<double> { 0, LengthMeters };
        for (double d = spacingMeters; d < LengthMeters; d += spacingMeters) distances.Add(d);
        foreach (var segment in route.Segments)
        {
            distances.Add(Math.Clamp(segment.StartMeters, 0, LengthMeters));
            distances.Add(Math.Clamp(segment.EndMeters, 0, LengthMeters));
        }
        var gps = route.Points?.OrderBy(p => p.DistanceMeters).ToArray();
        var usesLocalCoordinates = gps is { Length: >= 2 } && gps.All(point => point.LocalXMeters.HasValue && point.LocalZMeters.HasValue);
        _geographicOrigin = gps is { Length: >= 2 } && !usesLocalCoordinates
            && gps.All(point => point.Latitude.HasValue && point.Longitude.HasValue) ? gps[0] : null;
        _localOrigin = usesLocalCoordinates ? gps![0] : null;
        if (gps is { Length: >= 2 })
            foreach (var point in gps) distances.Add(Math.Clamp(point.DistanceMeters, 0, LengthMeters));
        if (_trackBanked && !usesLocalCoordinates)
            throw new ArgumentException("Velodrome geometry must use native local coordinates.", nameof(route));

        var raw = new List<(double DistanceMeters, Vector3 Position)>();
        double elevation = 0;
        double previousDistance = 0;
        foreach (var d in distances)
        {
            Vector3 position;
            if (usesLocalCoordinates)
                position = LocalPosition(gps!, d);
            else if (_geographicOrigin is not null)
                position = GpsPosition(gps!, d);
            else
            {
                // A closed circle provides a usable road for routes with only grades.
                // Integrate grade across intervals split at every segment boundary.
                elevation += (d - previousDistance) * route.GetGradientAt((d + previousDistance) / 2) / 100;
                var angle = d / LengthMeters * Math.Tau;
                var radius = LengthMeters / Math.Tau;
                position = new Vector3((float)(radius * (1 - Math.Cos(angle))),
                    (float)elevation, (float)(-radius * Math.Sin(angle)));
            }
            raw.Add((d, position));
            previousDistance = d;
        }
        if (_trackBanked && Vector3.Distance(raw[^1].Position, raw[0].Position) > 0.0001f)
            throw new ArgumentException("Velodrome reference line must close without correction.", nameof(route));
        ClosureCorrection = IsPointToPoint || _trackBanked ? Vector3.Zero : raw[^1].Position - raw[0].Position;
        var corrected = _trackBanked ? raw.ToArray() : raw.Select(v =>
        {
            if (IsPointToPoint) return (DistanceMeters: v.DistanceMeters, Position: v.Position);
            var t = Math.Clamp((v.DistanceMeters / LengthMeters - ClosureStartFraction)
                / (1 - ClosureStartFraction), 0, 1);
            var blend = (float)(t * t * (3 - 2 * t));
            return (DistanceMeters: v.DistanceMeters, Position: v.Position - ClosureCorrection * blend);
        }).ToArray();
        if (!IsPointToPoint && !_trackBanked)
            corrected[^1] = (corrected[^1].DistanceMeters, corrected[0].Position);

        // Segment boundaries can coincide with an authored GPS point after the
        // local-frame conversion to float. Collapse only those redundant samples;
        // retaining them would create equal arc-length chainages and zero-area ribbon faces.
        var distinctCorrected = new List<(double DistanceMeters, Vector3 Position)>(corrected.Length);
        for (var i = 0; i < corrected.Length; i++)
        {
            if (i > 0 && i < corrected.Length - 1
                && corrected[i].Position == corrected[i - 1].Position)
                continue;
            distinctCorrected.Add(corrected[i]);
        }
        corrected = distinctCorrected.ToArray();

        // Authored route metres and GPS geometry metres are not always identical.
        // Parameterize the centreline by its measured arc length, scaled to the
        // authoritative lap length, so the rider, ribbons and posts share one map.
        var cumulative = new double[corrected.Length];
        for (var i = 1; i < corrected.Length; i++)
            cumulative[i] = cumulative[i - 1] + Vector3.Distance(corrected[i - 1].Position, corrected[i].Position);
        if (cumulative[^1] <= 0 || !double.IsFinite(cumulative[^1]))
            throw new ArgumentException("Visual route centreline must have positive finite length.", nameof(route));
        CenterlineLengthMeters = cumulative[^1];
        if (_trackBanked && Math.Abs(CenterlineLengthMeters - LengthMeters) > 0.001)
            throw new ArgumentException($"Velodrome reference line measures {CenterlineLengthMeters:F6} m, not {LengthMeters:F3} m.", nameof(route));
        var scale = _trackBanked ? 1d : LengthMeters / cumulative[^1];
        var positions = corrected.Select(v => v.Position).ToArray();
        _samples = new VisualRouteSample[corrected.Length];
        for (var i = 0; i < corrected.Length; i++)
        {
            var previous = IsPointToPoint
                ? Math.Max(0, i - 1)
                : i == 0 || i == corrected.Length - 1 ? corrected.Length - 2 : i - 1;
            var next = IsPointToPoint
                ? Math.Min(corrected.Length - 1, i + 1)
                : i == 0 || i == corrected.Length - 1 ? 1 : i + 1;
            var tangent = positions[next] - positions[previous];
            if (tangent.LengthSquared() < 0.000001f)
                tangent = i == 0 ? positions[1] - positions[0] : positions[i] - positions[i - 1];
            _samples[i] = new VisualRouteSample(
                i == corrected.Length - 1 ? LengthMeters
                    : _trackBanked ? corrected[i].DistanceMeters : cumulative[i] * scale,
                positions[i], Vector3.Normalize(tangent));
        }
        GroundPlaneY = positions.Min(p => p.Y) - GroundClearanceMeters;
    }

    private static Vector3 GpsPosition(RoutePoint[] points, double distance)
    {
        var origin = points[0];
        var before = points.LastOrDefault(p => p.DistanceMeters <= distance) ?? origin;
        var after = points.FirstOrDefault(p => p.DistanceMeters >= distance) ?? points[^1];
        var t = after.DistanceMeters > before.DistanceMeters
            ? (distance - before.DistanceMeters) / (after.DistanceMeters - before.DistanceMeters) : 0;
        var lat = before.Latitude!.Value + (after.Latitude!.Value - before.Latitude.Value) * t;
        var lon = before.Longitude!.Value + (after.Longitude!.Value - before.Longitude.Value) * t;
        var altitude = before.ElevationMeters + (after.ElevationMeters - before.ElevationMeters) * t;
        const double radians = Math.PI / 180;
        return new Vector3(
            (float)((lon - origin.Longitude!.Value) * radians * EarthRadiusMeters * Math.Cos(origin.Latitude!.Value * radians)),
            (float)(altitude - origin.ElevationMeters),
            (float)(-(lat - origin.Latitude.Value) * radians * EarthRadiusMeters));
    }

    private static Vector3 LocalPosition(RoutePoint[] points, double distance)
    {
        var origin = points[0];
        var before = points.LastOrDefault(p => p.DistanceMeters <= distance) ?? origin;
        var after = points.FirstOrDefault(p => p.DistanceMeters >= distance) ?? points[^1];
        var t = after.DistanceMeters > before.DistanceMeters
            ? (distance - before.DistanceMeters) / (after.DistanceMeters - before.DistanceMeters) : 0;
        var x = before.LocalXMeters!.Value + (after.LocalXMeters!.Value - before.LocalXMeters.Value) * t;
        var z = before.LocalZMeters!.Value + (after.LocalZMeters!.Value - before.LocalZMeters.Value) * t;
        var altitude = before.ElevationMeters + (after.ElevationMeters - before.ElevationMeters) * t;
        return new Vector3((float)(x - origin.LocalXMeters!.Value), (float)(altitude - origin.ElevationMeters),
            (float)(z - origin.LocalZMeters!.Value));
    }

    public double WrapDistance(double distance)
    {
        if (!double.IsFinite(distance)) throw new ArgumentOutOfRangeException(nameof(distance));
        if (IsPointToPoint) return Math.Clamp(distance, 0, LengthMeters);
        return ((distance % LengthMeters) + LengthMeters) % LengthMeters;
    }

    public VisualRoutePose GetPoseAtDistance(double rideDistanceMeters)
    {
        var distance = WrapDistance(rideDistanceMeters);
        int low = 0, high = _samples.Length - 1;
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (_samples[middle].DistanceMeters <= distance) low = middle;
            else high = middle;
        }
        var a = _samples[low];
        var b = _samples[high];
        var t = (float)((distance - a.DistanceMeters) / (b.DistanceMeters - a.DistanceMeters));
        var forward = Vector3.Lerp(a.Forward, b.Forward, t);
        if (forward.LengthSquared() < 0.000001f) forward = b.Position - a.Position;
        var normalizedForward = Vector3.Normalize(forward);
        var bankSlope = _trackBanked ? VelodromeRouteGeometry.BankSlopeAt(distance) : 0f;
        return new VisualRoutePose(Vector3.Lerp(a.Position, b.Position, t), normalizedForward,
            distance, low, high, t) { BankSlope = bankSlope };
    }

    private readonly bool _trackBanked;
}
