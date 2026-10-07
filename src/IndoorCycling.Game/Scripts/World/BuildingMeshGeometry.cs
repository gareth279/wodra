using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

/// <summary>Small deterministic polygon extrusion shared by runtime and headless tests.</summary>
public static class BuildingMeshGeometry
{
    public static Vector3[] TriangulateSurface(IReadOnlyList<Vector3> closedRing)
    {
        if (closedRing.Count < 4 || Vector3.DistanceSquared(closedRing[0], closedRing[^1]) > .01f)
            throw new ArgumentException("A closed polygon ring needs at least three vertices.", nameof(closedRing));
        var floor = closedRing.Take(closedRing.Count - 1).ToArray();
        var result = new List<Vector3>(Math.Max(3, floor.Length - 2) * 3);
        foreach (var triangle in Triangulate(floor))
            result.AddRange([floor[triangle.A], floor[triangle.C], floor[triangle.B]]);
        return result.ToArray();
    }

    public static Vector3[] Extrude(IReadOnlyList<Vector3> closedFloorRing, float height)
    {
        if (!float.IsFinite(height) || height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (closedFloorRing.Count < 4) throw new ArgumentException("A closed footprint needs at least three vertices.", nameof(closedFloorRing));
        var floor = closedFloorRing.Take(closedFloorRing.Count - 1).ToArray();
        if (floor.Length < 3 || Vector3.DistanceSquared(floor[0], closedFloorRing[^1]) > .01f)
            throw new ArgumentException("Footprint ring must be closed.", nameof(closedFloorRing));
        var triangles = new List<Vector3>(floor.Length * 9);
        var signedArea = 0f;
        for (var i = 0; i < floor.Length; i++)
        {
            var a = floor[i]; var b = floor[(i + 1) % floor.Length];
            signedArea += a.X * b.Z - b.X * a.Z;
        }
        for (var i = 0; i < floor.Length; i++)
        {
            var a = floor[i]; var b = floor[(i + 1) % floor.Length];
            var at = a + Vector3.UnitY * height; var bt = b + Vector3.UnitY * height;
            if (signedArea > 0) triangles.AddRange([a, bt, b, a, at, bt]);
            else triangles.AddRange([a, b, bt, a, bt, at]);
        }
        foreach (var triangle in Triangulate(floor))
        {
            var a = floor[triangle.A] + Vector3.UnitY * height;
            var b = floor[triangle.B] + Vector3.UnitY * height;
            var c = floor[triangle.C] + Vector3.UnitY * height;
            triangles.AddRange([a, c, b]); // upward-facing in the XZ plane
        }
        return triangles.ToArray();
    }

    private readonly record struct Triangle(int A, int B, int C);
    private static IReadOnlyList<Triangle> Triangulate(IReadOnlyList<Vector3> polygon)
    {
        var area = 0f;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            area += a.X * b.Z - b.X * a.Z;
        }
        if (Math.Abs(area) < .0001f) throw new ArgumentException("Footprint has zero area.", nameof(polygon));
        var order = Enumerable.Range(0, polygon.Count).ToList();
        if (area < 0) order.Reverse();
        var result = new List<Triangle>();
        while (order.Count > 3)
        {
            var clipped = false;
            for (var i = 0; i < order.Count; i++)
            {
                var a = order[(i - 1 + order.Count) % order.Count]; var b = order[i]; var c = order[(i + 1) % order.Count];
                if (Cross(polygon[a], polygon[b], polygon[c]) <= .00001f) continue;
                if (order.Any(index => index != a && index != b && index != c
                    && Inside(polygon[index], polygon[a], polygon[b], polygon[c]))) continue;
                result.Add(new Triangle(a, b, c)); order.RemoveAt(i); clipped = true; break;
            }
            if (!clipped) throw new ArgumentException("Footprint cannot be triangulated safely.", nameof(polygon));
        }
        result.Add(new Triangle(order[0], order[1], order[2]));
        return result;
    }

    private static float Cross(Vector3 a, Vector3 b, Vector3 c) => (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
    private static bool Inside(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = Cross(a, b, p); var bc = Cross(b, c, p); var ca = Cross(c, a, p);
        return ab >= -.00001f && bc >= -.00001f && ca >= -.00001f;
    }
}
