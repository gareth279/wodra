using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

/// <summary>Finds mapped road surfaces that would be covered by a flat land-use ground overlay.</summary>
public static class VegetationGroundGeometry
{
    public static long? FindOverlappingRoadId(IReadOnlyList<Vector3> closedAreaRing,
        IReadOnlyList<TerrainRoadCorridor> roads, float edgeClearanceMeters = .2f)
    {
        if (closedAreaRing.Count < 4 || roads.Count == 0) return null;
        var polygon = closedAreaRing.Take(closedAreaRing.Count - 1).ToArray();
        if (polygon.Length < 3) return null;
        var minX = polygon.Min(point => point.X); var maxX = polygon.Max(point => point.X);
        var minZ = polygon.Min(point => point.Z); var maxZ = polygon.Max(point => point.Z);
        foreach (var road in roads)
        {
            if (road.Points.Count < 2 || !float.IsFinite(road.WidthMeters) || road.WidthMeters <= 0) continue;
            var radius = road.WidthMeters * .5f + Math.Max(0, edgeClearanceMeters);
            for (var segment = 1; segment < road.Points.Count; segment++)
            {
                var a = road.Points[segment - 1]; var b = road.Points[segment];
                if (Math.Max(a.X, b.X) < minX - radius || Math.Min(a.X, b.X) > maxX + radius
                    || Math.Max(a.Z, b.Z) < minZ - radius || Math.Min(a.Z, b.Z) > maxZ + radius) continue;
                if (Contains(a, polygon) || Contains(b, polygon)) return road.SourceOsmId;
                for (var edge = 0; edge < polygon.Length; edge++)
                {
                    var c = polygon[edge]; var d = polygon[(edge + 1) % polygon.Length];
                    if (SegmentDistanceSquared(a, b, c, d) <= radius * radius) return road.SourceOsmId;
                }
            }
        }
        return null;
    }

    private static bool Contains(Vector3 point, IReadOnlyList<Vector3> polygon)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + polygon.Count - 1) % polygon.Count];
            if ((a.Z > point.Z) != (b.Z > point.Z)
                && point.X < (b.X - a.X) * (point.Z - a.Z) / (b.Z - a.Z) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static float SegmentDistanceSquared(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        if (Intersects(a, b, c, d)) return 0;
        return Math.Min(Math.Min(PointSegmentDistanceSquared(a, c, d), PointSegmentDistanceSquared(b, c, d)),
            Math.Min(PointSegmentDistanceSquared(c, a, b), PointSegmentDistanceSquared(d, a, b)));
    }

    private static float PointSegmentDistanceSquared(Vector3 point, Vector3 a, Vector3 b)
    {
        var dx = b.X - a.X; var dz = b.Z - a.Z;
        var lengthSquared = dx * dx + dz * dz;
        var t = lengthSquared > 1e-8f
            ? Math.Clamp(((point.X - a.X) * dx + (point.Z - a.Z) * dz) / lengthSquared, 0, 1) : 0;
        var x = point.X - (a.X + dx * t); var z = point.Z - (a.Z + dz * t);
        return x * x + z * z;
    }

    private static bool Intersects(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        static float Cross(Vector3 p, Vector3 q, Vector3 r) => (q.X - p.X) * (r.Z - p.Z) - (q.Z - p.Z) * (r.X - p.X);
        static bool OnSegment(Vector3 p, Vector3 q, Vector3 r) => q.X >= Math.Min(p.X, r.X) - 1e-5f
            && q.X <= Math.Max(p.X, r.X) + 1e-5f && q.Z >= Math.Min(p.Z, r.Z) - 1e-5f
            && q.Z <= Math.Max(p.Z, r.Z) + 1e-5f;
        var abC = Cross(a, b, c); var abD = Cross(a, b, d);
        var cdA = Cross(c, d, a); var cdB = Cross(c, d, b);
        const float epsilon = 1e-5f;
        if (((abC > epsilon && abD < -epsilon) || (abC < -epsilon && abD > epsilon))
            && ((cdA > epsilon && cdB < -epsilon) || (cdA < -epsilon && cdB > epsilon))) return true;
        return Math.Abs(abC) <= epsilon && OnSegment(a, c, b)
            || Math.Abs(abD) <= epsilon && OnSegment(a, d, b)
            || Math.Abs(cdA) <= epsilon && OnSegment(c, a, d)
            || Math.Abs(cdB) <= epsilon && OnSegment(c, b, d);
    }
}
