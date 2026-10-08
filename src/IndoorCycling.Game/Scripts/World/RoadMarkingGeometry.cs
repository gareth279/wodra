using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

public enum RoadMarkingKind { ZebraStripe, StopLine, GiveWayDash, TrafficSignalLine }

public sealed record RoadMarkingControl(string Category, long OsmId,
    double NearestRouteDistanceMeters, double DistanceFromRouteMeters);

public sealed record RoadMarkingOptions
{
    public double MaximumCrossingDistanceFromRouteMeters { get; init; } = RoadMarkingGeometry.MaximumCrossingDistanceFromRouteMeters;
    public double MaximumControlDistanceFromRouteMeters { get; init; } = RoadMarkingGeometry.MaximumControlDistanceFromRouteMeters;
    public double DuplicateRouteDistanceMeters { get; init; } = RoadMarkingGeometry.DuplicateRouteDistanceMeters;
    public double DuplicateOrientationDegrees { get; init; } = RoadMarkingGeometry.DuplicateOrientationDegrees;
    public double ControlLineApproachMeters { get; init; } = RoadMarkingGeometry.ControlLineApproachMeters;
}

public readonly record struct RoadMarkingTriangle(RoadMarkingKind Kind, double RouteDistanceMeters,
    Vector3 A, Vector3 B, Vector3 C);

public sealed record RoadMarkingBuildResult(
    int CrossingSourcesConsidered, int NearbyCrossingSources, int ZebraCrossings,
    int CrossingDuplicatesSuppressed, int TrafficSignalLines, int StopLines, int GiveWayLines,
    IReadOnlyList<double> CrossingRouteDistancesMeters, IReadOnlyList<RoadMarkingTriangle> Triangles)
{
    public int MarkingTriangleCount => Triangles.Count;
}

/// <summary>Pure visual-only marking geometry derived from mapped OSM controls and the canonical ride route.</summary>
public static class RoadMarkingGeometry
{
    public const float DefaultRoadWidthMeters = 7f;
    public const double SpatialChunkLengthMeters = 400;
    public const float RoadMarkingYOffsetMeters = 0.006f;
    public const double MaximumCrossingDistanceFromRouteMeters = 8;
    public const double MaximumControlDistanceFromRouteMeters = 9;
    public const double DuplicateRouteDistanceMeters = 8;
    public const double DuplicateOrientationDegrees = 25;
    public const double ControlLineApproachMeters = 4.5;
    public const float ZebraStripeWidthMeters = 0.45f;
    public const float ZebraStripeGapMeters = 0.45f;
    public const int ZebraStripeCount = 4;

    private sealed record Selected(RoadMarkingControl Control, double Distance, Vector3 Forward);

    public static RoadMarkingBuildResult Build(VisualRoute route, IEnumerable<RoadMarkingControl> source,
        float roadWidthMeters = DefaultRoadWidthMeters, RoadMarkingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(source);
        if (!float.IsFinite(roadWidthMeters) || roadWidthMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(roadWidthMeters));
        options ??= new RoadMarkingOptions();
        if (!double.IsFinite(options.MaximumCrossingDistanceFromRouteMeters) || options.MaximumCrossingDistanceFromRouteMeters < 0
            || !double.IsFinite(options.MaximumControlDistanceFromRouteMeters) || options.MaximumControlDistanceFromRouteMeters < 0
            || !double.IsFinite(options.DuplicateRouteDistanceMeters) || options.DuplicateRouteDistanceMeters < 0
            || !double.IsFinite(options.DuplicateOrientationDegrees) || options.DuplicateOrientationDegrees is < 0 or > 180
            || !double.IsFinite(options.ControlLineApproachMeters) || options.ControlLineApproachMeters < 0)
            throw new ArgumentOutOfRangeException(nameof(options));

        var controls = source.Where(item => item is not null).ToArray();
        var crossingSources = controls.Where(item => item.Category == "crossing").ToArray();
        var nearbyCrossings = SelectNearby(route, crossingSources, options.MaximumCrossingDistanceFromRouteMeters);
        var crossings = Deduplicate(route, nearbyCrossings, options);
        var signalLines = Deduplicate(route, SelectNearby(route,
            controls.Where(item => item.Category == "traffic-signal"), options.MaximumControlDistanceFromRouteMeters), options);
        var stopLines = Deduplicate(route, SelectNearby(route,
            controls.Where(item => item.Category == "stop-sign"), options.MaximumControlDistanceFromRouteMeters), options);
        var giveWayLines = Deduplicate(route, SelectNearby(route,
            controls.Where(item => item.Category == "give-way-sign"), options.MaximumControlDistanceFromRouteMeters), options);

        var triangles = new List<RoadMarkingTriangle>();
        foreach (var crossing in crossings)
        {
            var firstOffset = -((ZebraStripeCount - 1) * (ZebraStripeWidthMeters + ZebraStripeGapMeters) / 2);
            for (var stripe = 0; stripe < ZebraStripeCount; stripe++)
            {
                var offset = firstOffset + stripe * (ZebraStripeWidthMeters + ZebraStripeGapMeters);
                var distance = route.WrapDistance(crossing.Distance + offset);
                AddRectangle(triangles, route, RoadMarkingKind.ZebraStripe, distance,
                    roadWidthMeters - 0.4f, ZebraStripeWidthMeters, 0);
            }
        }

        AddControlLines(signalLines, RoadMarkingKind.TrafficSignalLine, solid: true);
        AddControlLines(stopLines, RoadMarkingKind.StopLine, solid: true);
        AddControlLines(giveWayLines, RoadMarkingKind.GiveWayDash, solid: false);

        return new RoadMarkingBuildResult(crossingSources.Length, nearbyCrossings.Count, crossings.Count,
            nearbyCrossings.Count - crossings.Count, signalLines.Count, stopLines.Count, giveWayLines.Count,
            crossings.Select(item => item.Distance).ToArray(), triangles);

        void AddControlLines(IReadOnlyList<Selected> selected, RoadMarkingKind kind, bool solid)
        {
            foreach (var control in selected)
            {
                var lineDistance = route.WrapDistance(control.Distance - options.ControlLineApproachMeters);
                if (solid)
                {
                    AddRectangle(triangles, route, kind, lineDistance, roadWidthMeters - 0.4f, 0.14f, 0);
                    continue;
                }

                const int dashCount = 8;
                const float dashWidth = 0.58f;
                const float dashGap = 0.24f;
                var firstLateral = -((dashCount - 1) * (dashWidth + dashGap) / 2);
                for (var dash = 0; dash < dashCount; dash++)
                    AddRectangle(triangles, route, kind, lineDistance, dashWidth, 0.14f,
                        firstLateral + dash * (dashWidth + dashGap));
            }
        }
    }

    private static List<Selected> SelectNearby(VisualRoute route, IEnumerable<RoadMarkingControl> controls,
        double maximumDistance)
    {
        return controls.Where(item => double.IsFinite(item.NearestRouteDistanceMeters)
                && double.IsFinite(item.DistanceFromRouteMeters)
                && item.DistanceFromRouteMeters >= 0 && item.DistanceFromRouteMeters <= maximumDistance)
            .Select(item =>
            {
                var distance = route.WrapDistance(item.NearestRouteDistanceMeters);
                return new Selected(item, distance, route.GetPoseAtDistance(distance).Forward);
            })
            .OrderBy(item => item.Control.DistanceFromRouteMeters)
            .ThenBy(item => item.Distance)
            .ThenBy(item => item.Control.OsmId)
            .ToList();
    }

    private static List<Selected> Deduplicate(VisualRoute route, IReadOnlyList<Selected> candidates,
        RoadMarkingOptions options)
    {
        var accepted = new List<Selected>();
        var minimumDot = Math.Cos(options.DuplicateOrientationDegrees * Math.PI / 180);
        foreach (var candidate in candidates)
        {
            if (accepted.Any(existing => RouteDistance(route.LengthMeters, existing.Distance, candidate.Distance)
                    <= options.DuplicateRouteDistanceMeters
                && Vector3.Dot(Horizontal(existing.Forward), Horizontal(candidate.Forward)) >= minimumDot))
                continue;
            accepted.Add(candidate);
        }
        return accepted;
    }

    private static void AddRectangle(List<RoadMarkingTriangle> output, VisualRoute route, RoadMarkingKind kind,
        double routeDistance, float widthAcross, float lengthAlong, float lateralOffset)
    {
        var pose = route.GetPoseAtDistance(routeDistance);
        var forward = Vector3.Normalize(pose.Forward);
        var side = Vector3.Cross(Vector3.UnitY, forward);
        if (side.LengthSquared() < 1e-8f) return;
        side = Vector3.Normalize(side);
        var center = pose.Position + Vector3.UnitY * RoadMarkingYOffsetMeters + side * lateralOffset;
        var along = forward * (lengthAlong / 2);
        var across = side * (widthAcross / 2);
        var a = center - along - across;
        var b = center - along + across;
        var c = center + along + across;
        var d = center + along - across;
        AddUpward(a, c, b);
        AddUpward(a, d, c);

        void AddUpward(Vector3 first, Vector3 second, Vector3 third)
        {
            var normal = Vector3.Cross(second - first, third - first);
            if (!Finite(first) || !Finite(second) || !Finite(third)
                || !float.IsFinite(normal.LengthSquared()) || normal.LengthSquared() < 1e-10f)
                return;
            if (normal.Y < 0) (second, third) = (third, second);
            output.Add(new RoadMarkingTriangle(kind, routeDistance, first, second, third));
        }
    }

    private static double RouteDistance(double length, double first, double second)
    {
        var difference = Math.Abs(first - second);
        return Math.Min(difference, length - difference);
    }

    private static Vector3 Horizontal(Vector3 value)
    {
        value.Y = 0;
        return value.LengthSquared() < 1e-8f ? Vector3.Zero : Vector3.Normalize(value);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
