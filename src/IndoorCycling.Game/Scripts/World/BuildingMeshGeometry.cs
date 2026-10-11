using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

/// <summary>Small deterministic polygon extrusion shared by runtime and headless tests.</summary>
public static class BuildingMeshGeometry
{
    public sealed record StyledMesh(Vector3[] Facade, Vector3[] Roof, Vector3[] Windows,
        string EffectiveRoofShape, float WallTopHeightMeters, int WindowCount)
    {
        public int FacadeTriangleCount => Facade.Length / 3;
        public int RoofTriangleCount => Roof.Length / 3;
        public int WindowTriangleCount => Windows.Length / 3;
    }

    public static StyledMesh BuildStyled(IReadOnlyList<Vector3> closedFloorRing, float totalHeight,
        string? roofShape, float requestedRoofHeight, int storeys, bool addWindows,
        int maximumWindowColumnsPerWall = 8, int maximumStoreys = 6, int maximumWindowsPerBuilding = 120,
        uint facadePattern = 0)
    {
        if (!float.IsFinite(totalHeight) || totalHeight <= 0) throw new ArgumentOutOfRangeException(nameof(totalHeight));
        if (closedFloorRing.Count < 4 || Vector3.DistanceSquared(closedFloorRing[0], closedFloorRing[^1]) > .01f)
            throw new ArgumentException("Footprint ring must be closed.", nameof(closedFloorRing));
        var floor = closedFloorRing.Take(closedFloorRing.Count - 1).ToArray();
        var facade = new List<Vector3>();
        var roof = new List<Vector3>();
        var windows = new List<Vector3>();
        var shape = BuildingVisualStyle.NormalizeRoofShape(roofShape);
        var suitable = TryRectangle(floor, out var rectangle);
        if (shape is "gabled" or "hipped" && !suitable) shape = "flat";
        var footprintWidth = suitable ? rectangle.Width : ApproximateWidth(floor);
        var safeRoofHeight = float.IsFinite(requestedRoofHeight) ? requestedRoofHeight : 0;
        var roofHeight = shape == "flat" ? 0 : Math.Clamp(safeRoofHeight, 0, totalHeight * .4f);
        var wallTop = totalHeight - roofHeight;
        var baseY = floor.Average(point => point.Y);
        var roofBaseY = baseY;
        var signedArea = SignedArea(floor);
        for (var i = 0; i < floor.Length; i++)
        {
            var a = floor[i]; var b = floor[(i + 1) % floor.Length];
            // Keep the roofline level while each lower wall edge follows sampled
            // terrain at that footprint vertex.
            var at = new Vector3(a.X, baseY + wallTop, a.Z);
            var bt = new Vector3(b.X, baseY + wallTop, b.Z);
            if (signedArea > 0) facade.AddRange([a, bt, b, a, at, bt]);
            else facade.AddRange([a, b, bt, a, bt, at]);
        }

        if (shape == "flat")
        {
            foreach (var triangle in Triangulate(floor))
            {
                var a = new Vector3(floor[triangle.A].X, roofBaseY + totalHeight, floor[triangle.A].Z);
                var b = new Vector3(floor[triangle.B].X, roofBaseY + totalHeight, floor[triangle.B].Z);
                var c = new Vector3(floor[triangle.C].X, roofBaseY + totalHeight, floor[triangle.C].Z);
                roof.AddRange([a, c, b]);
            }
        }
        else if (shape == "gabled")
        {
            AddRoofQuad(roof, rectangle.Corner(-1, -1) + Vector3.UnitY * wallTop,
                rectangle.Corner(1, -1) + Vector3.UnitY * wallTop,
                rectangle.Ridge(1, totalHeight), rectangle.Ridge(-1, totalHeight));
            AddRoofQuad(roof, rectangle.Corner(1, 1) + Vector3.UnitY * wallTop,
                rectangle.Corner(-1, 1) + Vector3.UnitY * wallTop,
                rectangle.Ridge(-1, totalHeight), rectangle.Ridge(1, totalHeight));
            facade.AddRange([rectangle.Corner(-1,-1)+Vector3.UnitY*wallTop,
                rectangle.Corner(-1,1)+Vector3.UnitY*wallTop, rectangle.Ridge(-1,totalHeight)]);
            facade.AddRange([rectangle.Corner(1,1)+Vector3.UnitY*wallTop,
                rectangle.Corner(1,-1)+Vector3.UnitY*wallTop, rectangle.Ridge(1,totalHeight)]);
        }
        else // simple hipped roof with a ridge on the long axis
        {
            var ridgeHalf = Math.Max(0, (rectangle.Length - rectangle.Width) / 2);
            var leftRidge = rectangle.RidgeAt(-ridgeHalf, totalHeight);
            var rightRidge = rectangle.RidgeAt(ridgeHalf, totalHeight);
            AddRoofQuad(roof, rectangle.Corner(-1,-1)+Vector3.UnitY*wallTop,
                rectangle.Corner(1,-1)+Vector3.UnitY*wallTop, rightRidge, leftRidge);
            AddRoofQuad(roof, rectangle.Corner(1,1)+Vector3.UnitY*wallTop,
                rectangle.Corner(-1,1)+Vector3.UnitY*wallTop, leftRidge, rightRidge);
            AddRoofTriangle(roof, rectangle.Corner(-1,-1)+Vector3.UnitY*wallTop,
                rectangle.Corner(-1,1)+Vector3.UnitY*wallTop,leftRidge);
            AddRoofTriangle(roof, rectangle.Corner(1,1)+Vector3.UnitY*wallTop,
                rectangle.Corner(1,-1)+Vector3.UnitY*wallTop,rightRidge);
        }

        var windowCount = addWindows
            ? AddWindows(windows, floor, baseY + wallTop, Math.Clamp(storeys, 1, maximumStoreys),
                Math.Clamp(maximumWindowColumnsPerWall, 0, 8), Math.Clamp(maximumWindowsPerBuilding, 0, 160), facadePattern) : 0;
        _ = footprintWidth;
        return new StyledMesh(facade.ToArray(), roof.ToArray(), windows.ToArray(), shape, wallTop, windowCount);
    }

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

    private readonly record struct RectangleFrame(Vector3 Center, Vector3 Axis, Vector3 CrossAxis,
        float Length, float Width)
    {
        public Vector3 Corner(int along, int across) => Center + Axis * (along * Length / 2)
            + CrossAxis * (across * Width / 2);
        public Vector3 Ridge(int along, float y) => RidgeAt(along * Length / 2, y);
        public Vector3 RidgeAt(float alongMeters, float y) => Center + Axis * alongMeters + Vector3.UnitY * y;
    }

    private static bool TryRectangle(IReadOnlyList<Vector3> floor, out RectangleFrame frame)
    {
        frame = default;
        if (floor.Count != 4) return false;
        var edges = Enumerable.Range(0,4).Select(i => floor[(i+1)%4]-floor[i]).ToArray();
        var lengths = edges.Select(e => MathF.Sqrt(e.X*e.X+e.Z*e.Z)).ToArray();
        if (lengths.Any(length => !float.IsFinite(length) || length < 1.5f)) return false;
        var dot = Math.Abs(edges[0].X*edges[1].X + edges[0].Z*edges[1].Z)/(lengths[0]*lengths[1]);
        if (dot > .12 || Math.Abs(lengths[0]-lengths[2]) > Math.Max(lengths[0],lengths[2])*.15
            || Math.Abs(lengths[1]-lengths[3]) > Math.Max(lengths[1],lengths[3])*.15) return false;
        var axisEdge = lengths[0] >= lengths[1] ? edges[0] : edges[1];
        var length = Math.Max(lengths[0],lengths[1]); var width = Math.Min(lengths[0],lengths[1]);
        var axis = Vector3.Normalize(new Vector3(axisEdge.X,0,axisEdge.Z));
        var crossAxis = new Vector3(-axis.Z,0,axis.X);
        var center = floor.Aggregate(Vector3.Zero,(sum,p)=>sum+p)/floor.Count;
        var absCoords = floor.Select(p=>(Q:Vector3.Dot(p-center,axis),P:Vector3.Dot(p-center,crossAxis))).ToArray();
        if (absCoords.Any(point => Math.Abs(Math.Abs(point.Q)-length/2) > length*.2
            || Math.Abs(Math.Abs(point.P)-width/2) > width*.2)) return false;
        frame = new RectangleFrame(center,axis,crossAxis,length,width);
        return true;
    }

    private static float ApproximateWidth(IReadOnlyList<Vector3> floor)
    {
        var minX=floor.Min(p=>p.X);var maxX=floor.Max(p=>p.X);var minZ=floor.Min(p=>p.Z);var maxZ=floor.Max(p=>p.Z);
        return Math.Min(maxX-minX,maxZ-minZ);
    }

    private static float SignedArea(IReadOnlyList<Vector3> floor)
    {
        var area=0f;
        for(var i=0;i<floor.Count;i++){var a=floor[i];var b=floor[(i+1)%floor.Count];area+=a.X*b.Z-b.X*a.Z;}
        return area;
    }

    private static void AddQuad(List<Vector3> output, Vector3 a, Vector3 b, Vector3 c, Vector3 d) =>
        output.AddRange([a,b,c,a,c,d]);

    private static void AddRoofQuad(List<Vector3> output, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        AddRoofTriangle(output,a,b,c);AddRoofTriangle(output,a,c,d);
    }

    private static void AddRoofTriangle(List<Vector3> output, Vector3 a, Vector3 b, Vector3 c)
    {
        if (Vector3.Cross(b-a,c-a).Y < 0) (b,c)=(c,b);
        output.AddRange([a,b,c]);
    }

    private static int AddWindows(List<Vector3> output, IReadOnlyList<Vector3> floor, float wallTopY,
        int storeys, int maxColumnsPerWall, int maxWindows, uint facadePattern)
    {
        if (maxColumnsPerWall == 0 || maxWindows == 0) return 0;
        var area=SignedArea(floor);var emitted=0;
        for(var i=0;i<floor.Count && emitted<maxWindows;i++)
        {
            var a=floor[i];var b=floor[(i+1)%floor.Count];
            var dx=b.X-a.X;var dz=b.Z-a.Z;var length=MathF.Sqrt(dx*dx+dz*dz);
            if(length<4.4f)continue;
            var edgeWallHeight = wallTopY - Math.Max(a.Y, b.Y);
            if (edgeWallHeight <= 1.2f) continue;
            var levelHeight = edgeWallHeight / storeys;
            var columns=Math.Min(maxColumnsPerWall,(int)MathF.Floor((length-2f)/2.1f));
            if(columns<1)continue;
            var outward=area>0?new Vector3(dz,0,-dx):new Vector3(-dz,0,dx);
            outward=Vector3.Normalize(outward)*.012f;
            var width=Math.Min(1.15f,(length-2)/columns*.52f);
            var height=Math.Min(.9f,levelHeight*.48f);
            for(var level=0;level<storeys && emitted<maxWindows;level++)
            for(var column=0;column<columns && emitted<maxWindows;column++)
            {
                var hash = WindowHash(facadePattern, i, level, column);
                // A stable per-building pattern breaks up the identical window grid
                // without introducing per-frame randomness or additional nodes.
                if (hash % 13 == 0) continue;
                var centerT=(column+1f)/(columns+1f);
                var center=a+(b-a)*centerT+outward;
                var widthScale = .78f + ((hash >> 8) & 0xff) / 255f * .22f;
                var heightScale = .84f + ((hash >> 16) & 0xff) / 255f * .16f;
                var actualWidth = width * widthScale;
                var actualHeight = height * heightScale;
                var u=new Vector3(dx/length,0,dz/length)*(actualWidth/2);
                var low=center+Vector3.UnitY*(level*levelHeight+(levelHeight-actualHeight)/2);
                var high=low+Vector3.UnitY*actualHeight;
                if(area>0) AddQuad(output,low-u,high-u,high+u,low+u);
                else AddQuad(output,low+u,high+u,high-u,low-u);
                emitted++;
            }
        }
        return emitted;
    }

    private static uint WindowHash(uint seed, int wall, int level, int column)
    {
        unchecked
        {
            var value = seed ^ ((uint)wall * 0x9E3779B9u) ^ ((uint)level * 0x85EBCA6Bu) ^ ((uint)column * 0xC2B2AE35u);
            value ^= value >> 16; value *= 0x7FEB352Du; value ^= value >> 15; value *= 0x846CA68Bu; value ^= value >> 16;
            return value;
        }
    }
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
