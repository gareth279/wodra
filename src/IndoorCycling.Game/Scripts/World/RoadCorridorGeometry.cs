using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

public readonly record struct RoadFootprintClearance(bool Intersects, float ClearanceMeters,
    double RouteChainageMeters, int RoadSegmentIndex, long SourceOsmId = 0);
public readonly record struct RoadFootprintPlacement(float SourceLateralMeters, float VisualLateralMeters,
    RoadFootprintClearance Clearance, bool Adjusted, bool Resolved);
public readonly record struct RoadFootprintRoutePlacement(float SourceLateralMeters, float SourceForwardMeters,
    float VisualLateralMeters, float VisualForwardMeters, RoadFootprintClearance Clearance,
    bool Adjusted, bool Resolved);
public readonly record struct RoadFootprintDimensions(float LengthMeters, float WidthMeters);

/// <summary>
/// Load-time spatial index over the same bounded-miter road ribbon quads used by ProceduralRoad.
/// Queries use conservative, rotated horizontal object footprints; no scene nodes or physics are needed.
/// </summary>
public sealed class RoadCorridorGeometry
{
    private const float CellMeters = 24f;
    public const float RoadsideClearanceMeters = .25f;
    private sealed record RoadQuad(Vector2[] Polygon, double Chainage, int Segment, long SourceOsmId, bool IsBackground,
        float MinX, float MaxX, float MinZ, float MaxZ);
    private readonly Dictionary<(int X, int Z), List<RoadQuad>> _cells = [];
    private readonly List<RoadQuad> _quads = [];
    public float RoadWidthMeters { get; }
    public float ProtectedHalfWidthMeters { get; }
    public int IndexedQuadCount => _quads.Count;

    public static RoadFootprintDimensions? AuthoredFeatureFootprint(string objectType, double lengthMeters,
        double widthMeters) => objectType switch
    {
        // BuildHedge() adds rounded spheres beyond the ends of its continuous body.
        // The extra 0.8m conservatively contains the outermost canopy lobes.
        "hedge" or "shrub" or "planting-bed" =>
            new((float)Math.Max(1, lengthMeters + .8), (float)Math.Max(.25, widthMeters)),
        "timber-fence" or "picket-fence" or "low-boundary-wall" or "retaining-wall" =>
            new((float)Math.Max(.5, lengthMeters), (float)Math.Max(.2, widthMeters)),
        "broadleaf-tree" or "conifer-tree" =>
            new((float)Math.Max(.4, widthMeters * .2), (float)Math.Max(.4, widthMeters * .2)),
        "utility-pole" => new(.35f, .35f),
        _ => null
    };

    public RoadCorridorGeometry(VisualRoute route, float roadWidthMeters,
        float maximumCyclistCenterOffsetMeters, float cyclistBodyMarginMeters)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!float.IsFinite(roadWidthMeters) || roadWidthMeters <= 0
            || !float.IsFinite(maximumCyclistCenterOffsetMeters) || maximumCyclistCenterOffsetMeters < 0
            || !float.IsFinite(cyclistBodyMarginMeters) || cyclistBodyMarginMeters < 0)
            throw new ArgumentOutOfRangeException(nameof(roadWidthMeters));
        RoadWidthMeters = roadWidthMeters;
        ProtectedHalfWidthMeters = Math.Max(roadWidthMeters * .5f,
            maximumCyclistCenterOffsetMeters + cyclistBodyMarginMeters) + RoadsideClearanceMeters;
        var positions = route.Samples.Select(sample => sample.Position).ToArray();
        // Expand the exact ribbon construction by the rider envelope and a shared roadside gap.
        var quads = RoadRibbonMath.BuildQuads(positions, ProtectedHalfWidthMeters * 2);
        for (var i = 0; i < quads.Count; i++)
        {
            var q = quads[i];
            var polygon = new[] { XZ(q.StartLeft), XZ(q.EndLeft), XZ(q.EndRight), XZ(q.StartRight) };
            AddQuad(polygon,
                (route.Samples[i].DistanceMeters + route.Samples[i + 1].DistanceMeters) * .5, i, 0);
        }
        var last = route.Samples.Count - 1;
        for (var i = 0; i < last; i++)
        {
            var previous = route.Samples[i == 0 ? last - 1 : i - 1].Position;
            var current = route.Samples[i].Position;
            var next = route.Samples[i + 1].Position;
            if (RoadRibbonMath.ClassifyJoin(previous, current, next) != RoadJoinType.Discontinuity) continue;
            var radius = RoadRibbonMath.SharpJoinFillRadius(previous, current, next, ProtectedHalfWidthMeters * 2);
            foreach (var triangle in RoadRibbonMath.BuildSharpJoinFill(current, radius))
                AddQuad([XZ(triangle.A), XZ(triangle.B), XZ(triangle.C)], route.Samples[i].DistanceMeters, i, 0);
        }
    }

    /// <summary>Adds mapped side-street ribbons to the same no-object corridor index.</summary>
    public void AddBackgroundRoads(IEnumerable<TerrainRoadCorridor> roads)
    {
        foreach (var road in roads)
        {
            var width = Math.Max(road.WidthMeters, 1) + RoadsideClearanceMeters * 2;
            foreach (var quad in BackgroundStreetGeometry.BuildRibbon(road.Points, width))
                AddQuad([XZ(quad.StartLeft), XZ(quad.EndLeft), XZ(quad.EndRight), XZ(quad.StartRight)],
                    -1, -1, road.SourceOsmId, true);
        }
    }

    /// <summary>Measures a box centered in route-local lateral/forward coordinates at one chainage.</summary>
    public RoadFootprintClearance Measure(VisualRoute route, double chainageMeters, float lateralMeters,
        float forwardMeters, float lengthMeters, float widthMeters, float rotationDegrees = 0)
    {
        if (!float.IsFinite(lateralMeters + forwardMeters + lengthMeters + widthMeters + rotationDegrees)
            || lengthMeters <= 0 || widthMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(lengthMeters));
        var pose = route.GetPoseAtDistance(chainageMeters);
        var forward = Horizontal(pose.Forward);
        if (forward.LengthSquared() < 1e-8f) return new(false, float.PositiveInfinity, chainageMeters, -1);
        var side = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        var center = pose.Position + side * lateralMeters + forward * forwardMeters;
        var radians = rotationDegrees * MathF.PI / 180;
        var cos = MathF.Cos(radians); var sin = MathF.Sin(radians);
        var footprint = new Vector2[4];
        var corners = new[] { new Vector2(-lengthMeters / 2, -widthMeters / 2),
            new Vector2(-lengthMeters / 2, widthMeters / 2), new Vector2(lengthMeters / 2, widthMeters / 2),
            new Vector2(lengthMeters / 2, -widthMeters / 2) };
        for (var i = 0; i < corners.Length; i++)
        {
            // Authored local X is cross-track; local Z is along-track at zero rotation.
            var lateral = corners[i].X * cos + corners[i].Y * sin;
            var along = -corners[i].X * sin + corners[i].Y * cos;
            var point = center + side * lateral + forward * along;
            footprint[i] = XZ(point);
        }
        return MeasurePolygon(footprint);
    }

    /// <summary>Measures an arbitrary horizontal footprint, including OSM building rings.</summary>
    public RoadFootprintClearance MeasurePolygon(IReadOnlyList<Vector2> footprint) => MeasurePolygon(footprint, null);
    public RoadFootprintClearance MeasureRoutePolygon(IReadOnlyList<Vector2> footprint) => MeasurePolygon(footprint, false);
    public RoadFootprintClearance MeasureBackgroundPolygon(IReadOnlyList<Vector2> footprint) => MeasurePolygon(footprint, true);

    private RoadFootprintClearance MeasurePolygon(IReadOnlyList<Vector2> footprint, bool? backgroundOnly)
    {
        if (footprint is null || footprint.Count < 3 || footprint.Any(point => !float.IsFinite(point.X + point.Y)))
            throw new ArgumentException("A finite polygon footprint is required.", nameof(footprint));
        // Use the convex hull as a conservative bound for concave OSM building outlines.
        footprint = ConvexHull(footprint);
        var minX = footprint.Min(p => p.X); var maxX = footprint.Max(p => p.X);
        var minZ = footprint.Min(p => p.Y); var maxZ = footprint.Max(p => p.Y);
        var candidates = new HashSet<RoadQuad>();
        for (var x = Cell(minX); x <= Cell(maxX); x++)
        for (var z = Cell(minZ); z <= Cell(maxZ); z++)
            if (_cells.TryGetValue((x, z), out var entries)) candidates.UnionWith(entries);
        var best = new RoadFootprintClearance(false, float.PositiveInfinity, 0, -1);
        foreach (var road in candidates)
        {
            if (backgroundOnly is { } isBackground && road.IsBackground != isBackground) continue;
            if (road.MaxX < minX || road.MinX > maxX || road.MaxZ < minZ || road.MinZ > maxZ) continue;
            var intrusion = PenetrationDepth(footprint, road.Polygon);
            var intersects = intrusion > .001f;
            var clearance = intersects ? -intrusion : PolygonDistance(footprint, road.Polygon);
            if (clearance > best.ClearanceMeters
                || Math.Abs(clearance - best.ClearanceMeters) < 1e-6f
                    && (road.Chainage < best.RouteChainageMeters || best.RoadSegmentIndex < 0)) continue;
            best = new RoadFootprintClearance(intersects, clearance, road.Chainage, road.Segment, road.SourceOsmId);
        }
        if (float.IsPositiveInfinity(best.ClearanceMeters))
        {
            const float clearanceSearchMeters = 24;
            var nearby = new HashSet<RoadQuad>();
            for (var x = Cell(minX - clearanceSearchMeters); x <= Cell(maxX + clearanceSearchMeters); x++)
            for (var z = Cell(minZ - clearanceSearchMeters); z <= Cell(maxZ + clearanceSearchMeters); z++)
                if (_cells.TryGetValue((x, z), out var entries)) nearby.UnionWith(entries);
            var nearestClearance = float.PositiveInfinity;
            var nearestRoad = default(RoadQuad);
            foreach (var road in nearby)
            {
                if (backgroundOnly is { } isBackground && road.IsBackground != isBackground) continue;
                if (road.MaxX < minX - clearanceSearchMeters || road.MinX > maxX + clearanceSearchMeters
                    || road.MaxZ < minZ - clearanceSearchMeters || road.MinZ > maxZ + clearanceSearchMeters) continue;
                var distance = PolygonDistance(footprint, road.Polygon);
                if (distance >= nearestClearance) continue;
                nearestClearance = distance;
                nearestRoad = road;
            }
            best = float.IsPositiveInfinity(nearestClearance)
                ? new(false, clearanceSearchMeters, 0, -1)
                : new(false, nearestClearance, nearestRoad!.Chainage, nearestRoad.Segment, nearestRoad.SourceOsmId);
        }
        return best;
    }

    public RoadFootprintPlacement FindSafeLateralPlacement(VisualRoute route, double chainageMeters,
        float sourceLateralMeters, float forwardMeters, float lengthMeters, float widthMeters,
        float rotationDegrees = 0, float maximumShiftMeters = 16, float searchStepMeters = .25f)
    {
        if (!float.IsFinite(maximumShiftMeters) || maximumShiftMeters < 0
            || !float.IsFinite(searchStepMeters) || searchStepMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumShiftMeters));
        var initial = Measure(route, chainageMeters, sourceLateralMeters, forwardMeters,
            lengthMeters, widthMeters, rotationDegrees);
        if (!initial.Intersects && initial.ClearanceMeters >= RoadsideClearanceMeters)
            return new(sourceLateralMeters, sourceLateralMeters, initial, false, true);
        var side = sourceLateralMeters < 0 ? -1f : 1f;
        var last = initial;
        for (var shift = searchStepMeters; shift <= maximumShiftMeters + 1e-5f; shift += searchStepMeters)
        {
            var candidate = sourceLateralMeters + side * shift;
            var measured = Measure(route, chainageMeters, candidate, forwardMeters,
                lengthMeters, widthMeters, rotationDegrees);
            last = measured;
            if (measured.Intersects || measured.ClearanceMeters < RoadsideClearanceMeters) continue;
            return new(sourceLateralMeters, candidate, measured, true, true);
        }
        return new(sourceLateralMeters, sourceLateralMeters, last, false, false);
    }

    /// <summary>Finds the nearest safe roadside point in a deterministic outward half-disc, useful at junctions.</summary>
    public RoadFootprintRoutePlacement FindSafeRoutePlacement(VisualRoute route, double chainageMeters,
        float sourceLateralMeters, float sourceForwardMeters, float lengthMeters, float widthMeters,
        float rotationDegrees = 0, float maximumShiftMeters = 12, float searchStepMeters = .5f)
    {
        if (!float.IsFinite(maximumShiftMeters) || maximumShiftMeters < 0
            || !float.IsFinite(searchStepMeters) || searchStepMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumShiftMeters));
        var initial = Measure(route, chainageMeters, sourceLateralMeters, sourceForwardMeters,
            lengthMeters, widthMeters, rotationDegrees);
        if (!initial.Intersects && initial.ClearanceMeters >= RoadsideClearanceMeters)
            return new(sourceLateralMeters, sourceForwardMeters, sourceLateralMeters, sourceForwardMeters,
                initial, false, true);
        var side = sourceLateralMeters < 0 ? -1f : 1f;
        var last = initial;
        var angleCount = 16;
        for (var radius = searchStepMeters; radius <= maximumShiftMeters + 1e-5f; radius += searchStepMeters)
        for (var angleIndex = 0; angleIndex <= angleCount; angleIndex++)
        {
            var angle = (-MathF.PI / 2) + MathF.PI * angleIndex / angleCount;
            var lateral = sourceLateralMeters + side * MathF.Cos(angle) * radius;
            var forward = sourceForwardMeters + MathF.Sin(angle) * radius;
            var measured = Measure(route, chainageMeters, lateral, forward,
                lengthMeters, widthMeters, rotationDegrees);
            last = measured;
            if (measured.Intersects || measured.ClearanceMeters < RoadsideClearanceMeters) continue;
            return new(sourceLateralMeters, sourceForwardMeters, lateral, forward, measured, true, true);
        }
        return new(sourceLateralMeters, sourceForwardMeters, sourceLateralMeters, sourceForwardMeters,
            last, false, false);
    }

    private void AddToCells(RoadQuad item)
    {
        for (var x = Cell(item.MinX); x <= Cell(item.MaxX); x++)
        for (var z = Cell(item.MinZ); z <= Cell(item.MaxZ); z++)
        {
            if (!_cells.TryGetValue((x, z), out var bucket)) _cells[(x, z)] = bucket = [];
            bucket.Add(item);
        }
    }

    private void AddQuad(Vector2[] polygon, double chainage, int segment, long sourceOsmId, bool isBackground = false)
    {
        if (polygon.Length < 3 || polygon.Any(point => !float.IsFinite(point.X + point.Y))) return;
        var item = new RoadQuad(polygon, chainage, segment, sourceOsmId, isBackground,
            polygon.Min(p => p.X), polygon.Max(p => p.X), polygon.Min(p => p.Y), polygon.Max(p => p.Y));
        _quads.Add(item);
        AddToCells(item);
    }

    private static float PenetrationDepth(IReadOnlyList<Vector2> a, IReadOnlyList<Vector2> b)
    {
        var minimumOverlap = float.PositiveInfinity;
        for (var i = 0; i < a.Count; i++)
        {
            var edge = a[(i + 1) % a.Count] - a[i];
            if (edge.LengthSquared() < 1e-10f) continue;
            var axis = Vector2.Normalize(new Vector2(-edge.Y, edge.X));
            var aMin = a.Min(p => Vector2.Dot(p, axis)); var aMax = a.Max(p => Vector2.Dot(p, axis));
            var bMin = b.Min(p => Vector2.Dot(p, axis)); var bMax = b.Max(p => Vector2.Dot(p, axis));
            var overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
            if (overlap < 0) return -1;
            minimumOverlap = Math.Min(minimumOverlap, overlap);
        }
        for (var i = 0; i < b.Count; i++)
        {
            var edge = b[(i + 1) % b.Count] - b[i];
            if (edge.LengthSquared() < 1e-10f) continue;
            var axis = Vector2.Normalize(new Vector2(-edge.Y, edge.X));
            var aMin = a.Min(p => Vector2.Dot(p, axis)); var aMax = a.Max(p => Vector2.Dot(p, axis));
            var bMin = b.Min(p => Vector2.Dot(p, axis)); var bMax = b.Max(p => Vector2.Dot(p, axis));
            var overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
            if (overlap < 0) return -1;
            minimumOverlap = Math.Min(minimumOverlap, overlap);
        }
        return float.IsFinite(minimumOverlap) ? minimumOverlap : -1;
    }
    private static float PolygonDistance(IReadOnlyList<Vector2> a, IReadOnlyList<Vector2> b)
    {
        var best = float.PositiveInfinity;
        for (var i = 0; i < a.Count; i++)
        for (var j = 0; j < b.Count; j++)
        {
            best = Math.Min(best, SegmentDistance(a[i], b[j], b[(j + 1) % b.Count]));
            best = Math.Min(best, SegmentDistance(b[j], a[i], a[(i + 1) % a.Count]));
        }
        return best;
    }
    private static float SegmentDistance(Vector2 point, Vector2 a, Vector2 b)
    {
        var delta = b - a; var length = delta.LengthSquared();
        var t = length > 1e-10f ? Math.Clamp(Vector2.Dot(point - a, delta) / length, 0, 1) : 0;
        return Vector2.Distance(point, a + delta * t);
    }
    private static Vector2[] ConvexHull(IReadOnlyList<Vector2> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (sorted.Length <= 3) return sorted;
        var hull = new List<Vector2>(sorted.Length * 2);
        foreach (var p in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^1] - hull[^2], p - hull[^1]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        var lower = hull.Count;
        for (var i = sorted.Length - 2; i >= 0; i--)
        {
            var p = sorted[i];
            while (hull.Count > lower && Cross(hull[^1] - hull[^2], p - hull[^1]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull.ToArray();
    }
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    private static Vector2 XZ(Vector3 value) => new(value.X, value.Z);
    private static Vector3 Horizontal(Vector3 value) { value.Y = 0; return value; }
    private static int Cell(float value) => (int)MathF.Floor(value / CellMeters);
}
