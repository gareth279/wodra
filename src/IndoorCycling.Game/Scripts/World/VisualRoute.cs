using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

public readonly record struct VisualRouteVertex(double DistanceMeters, Vector3 Position);
public readonly record struct VisualRoutePose(Vector3 Position, Vector3 Forward, double LoopDistanceMeters);

/// <summary>Rendering geometry keyed by domain distance, not by geometric arc length.
/// Coordinates: +X east, +Y up, -Z north. One unit is one metre.
/// No trainer or Godot dependencies; route definitions remain authoritative.</summary>
public sealed class VisualRoute
{
    private const double EarthRadiusMeters = 6371000;
    private readonly VisualRouteVertex[] _vertices;
    public IReadOnlyList<VisualRouteVertex> Vertices => _vertices;
    public double LengthMeters { get; }
    public Vector3 ClosureCorrection { get; }

    // Synthetic/nonclosed data is visually closed over the last quarter of a lap.
    // This affects rendering only, never route grades, ride distance or FIT samples.
    public const double ClosureStartFraction = 0.75;

    public VisualRoute(RouteDefinition route, double spacingMeters = 10)
    {
        if (!double.IsFinite(route.DistanceMeters) || route.DistanceMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(route), "Visual routes require a positive length.");
        if (!double.IsFinite(spacingMeters) || spacingMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(spacingMeters));
        LengthMeters = route.DistanceMeters;
        var distances = new SortedSet<double> { 0, LengthMeters };
        for (double d = spacingMeters; d < LengthMeters; d += spacingMeters) distances.Add(d);
        foreach (var segment in route.Segments)
        {
            distances.Add(Math.Clamp(segment.StartMeters, 0, LengthMeters));
            distances.Add(Math.Clamp(segment.EndMeters, 0, LengthMeters));
        }
        var gps = route.Points?.OrderBy(p => p.DistanceMeters).ToArray();
        if (gps is { Length: >= 2 })
            foreach (var point in gps) distances.Add(Math.Clamp(point.DistanceMeters, 0, LengthMeters));

        var raw = new List<VisualRouteVertex>();
        double elevation = 0;
        double previousDistance = 0;
        foreach (var d in distances)
        {
            Vector3 position;
            if (gps is { Length: >= 2 })
                position = GpsPosition(gps, d);
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
            raw.Add(new VisualRouteVertex(d, position));
            previousDistance = d;
        }
        ClosureCorrection = raw[^1].Position - raw[0].Position;
        _vertices = raw.Select(v =>
        {
            var t = Math.Clamp((v.DistanceMeters / LengthMeters - ClosureStartFraction)
                / (1 - ClosureStartFraction), 0, 1);
            var blend = (float)(t * t * (3 - 2 * t));
            return v with { Position = v.Position - ClosureCorrection * blend };
        }).ToArray();
        _vertices[^1] = _vertices[^1] with { Position = _vertices[0].Position };
    }

    private static Vector3 GpsPosition(RoutePoint[] points, double distance)
    {
        var origin = points[0];
        var before = points.LastOrDefault(p => p.DistanceMeters <= distance) ?? origin;
        var after = points.FirstOrDefault(p => p.DistanceMeters >= distance) ?? points[^1];
        var t = after.DistanceMeters > before.DistanceMeters
            ? (distance - before.DistanceMeters) / (after.DistanceMeters - before.DistanceMeters) : 0;
        var lat = before.Latitude + (after.Latitude - before.Latitude) * t;
        var lon = before.Longitude + (after.Longitude - before.Longitude) * t;
        var altitude = before.ElevationMeters + (after.ElevationMeters - before.ElevationMeters) * t;
        const double radians = Math.PI / 180;
        return new Vector3(
            (float)((lon - origin.Longitude) * radians * EarthRadiusMeters * Math.Cos(origin.Latitude * radians)),
            (float)(altitude - origin.ElevationMeters),
            (float)(-(lat - origin.Latitude) * radians * EarthRadiusMeters));
    }

    public double WrapDistance(double distance)
    {
        if (!double.IsFinite(distance)) throw new ArgumentOutOfRangeException(nameof(distance));
        return ((distance % LengthMeters) + LengthMeters) % LengthMeters;
    }

    private Vector3 PositionAt(double wrappedDistance)
    {
        int low = 0, high = _vertices.Length - 1;
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (_vertices[middle].DistanceMeters <= wrappedDistance) low = middle;
            else high = middle;
        }
        var a = _vertices[low];
        var b = _vertices[high];
        var t = (float)((wrappedDistance - a.DistanceMeters) / (b.DistanceMeters - a.DistanceMeters));
        return Vector3.Lerp(a.Position, b.Position, t);
    }

    public VisualRoutePose GetPoseAtDistance(double rideDistanceMeters)
    {
        var distance = WrapDistance(rideDistanceMeters);
        // Symmetric lookaround smooths direction changes and also handles the lap seam.
        var lookaround = Math.Min(8, LengthMeters / 8);
        var tangent = PositionAt(WrapDistance(distance + lookaround))
            - PositionAt(WrapDistance(distance - lookaround));
        if (tangent.LengthSquared() < 0.000001f) tangent = -Vector3.UnitZ;
        return new VisualRoutePose(PositionAt(distance), Vector3.Normalize(tangent), distance);
    }
}
