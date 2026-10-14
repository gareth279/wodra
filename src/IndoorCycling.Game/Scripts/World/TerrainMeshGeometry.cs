using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

public sealed record TerrainChunkMeshData(string Id, Vector3[] Vertices, Vector3[] Normals, int[] Indices);
public sealed record TerrainZoneChunk(RouteTerrainChunkData Chunk, double SpacingMeters, double VerticalBiasMeters, bool ApplyRoadClearance);
public sealed record TerrainRoadCorridor(IReadOnlyList<Vector3> Points, float WidthMeters, long SourceOsmId = 0);
public sealed record TerrainMeshBuildResult(TerrainChunkMeshData[] Chunks, int AdjustedForRoadClearanceVertices,
    int TriangleCount, int UniqueVertexCount, int RoadCutoutCellCount = 0);

public static class RouteGroundFallbackPolicy
{
    public static bool ShouldAddLegacyApron(bool sampledTerrainAvailable) => !sampledTerrainAvailable;
}

/// <summary>Godot-independent terrain mesh builder used by runtime and focused geometry tests.</summary>
public static class TerrainMeshGeometry
{
    private readonly record struct GridKey(long X, long Z, long Zone);
    private sealed record Triangle(Vector3 A, Vector3 B, Vector3 C, GridKey KeyA, GridKey KeyB, GridKey KeyC);
    private sealed class RouteSegmentSpatialIndex
    {
        private const double BucketSizeMeters = 50;
        private readonly IReadOnlyList<VisualRouteSample> _samples;
        private readonly Dictionary<(int X, int Z), List<int>> _segmentsByBucket = [];

        public RouteSegmentSpatialIndex(IReadOnlyList<VisualRouteSample> samples)
        {
            _samples = samples;
            for (var i = 0; i < samples.Count - 1; i++)
            {
                var a = samples[i].Position;
                var b = samples[i + 1].Position;
                var minX = Bucket(Math.Min(a.X, b.X)); var maxX = Bucket(Math.Max(a.X, b.X));
                var minZ = Bucket(Math.Min(a.Z, b.Z)); var maxZ = Bucket(Math.Max(a.Z, b.Z));
                for (var x = minX; x <= maxX; x++)
                for (var z = minZ; z <= maxZ; z++)
                {
                    if (!_segmentsByBucket.TryGetValue((x, z), out var segments))
                        _segmentsByBucket[(x, z)] = segments = [];
                    segments.Add(i);
                }
            }
        }

        public bool TryGetNearest(Vector3 point, double radiusMeters, out float roadY)
        {
            var radiusSquared = radiusMeters * radiusMeters;
            var bestDistanceSquared = radiusSquared;
            roadY = 0;
            var found = false;
            var centerX = Bucket(point.X); var centerZ = Bucket(point.Z);
            var range = Math.Max(1, (int)Math.Ceiling(radiusMeters / BucketSizeMeters));
            for (var x = centerX - range; x <= centerX + range; x++)
            for (var z = centerZ - range; z <= centerZ + range; z++)
            {
                if (!_segmentsByBucket.TryGetValue((x, z), out var segments)) continue;
                foreach (var index in segments)
                {
                    var a = _samples[index].Position;
                    var b = _samples[index + 1].Position;
                    var dx = b.X - a.X; var dz = b.Z - a.Z;
                    var lengthSquared = dx * dx + dz * dz;
                    var t = lengthSquared > 1e-8f
                        ? Math.Clamp(((point.X - a.X) * dx + (point.Z - a.Z) * dz) / lengthSquared, 0, 1)
                        : 0;
                    var nearestX = a.X + dx * t; var nearestZ = a.Z + dz * t;
                    var xDistance = point.X - nearestX; var zDistance = point.Z - nearestZ;
                    var distanceSquared = xDistance * xDistance + zDistance * zDistance;
                    if (distanceSquared > bestDistanceSquared) continue;
                    bestDistanceSquared = distanceSquared;
                    roadY = a.Y + (b.Y - a.Y) * t;
                    found = true;
                }
            }
            return found;
        }

        private static int Bucket(double coordinate) => (int)Math.Floor(coordinate / BucketSizeMeters);
    }
    private sealed class BackgroundRoadIndex
    {
        private const double BucketSizeMeters=50;
        private readonly List<(Vector3 A,Vector3 B,float HalfWidth)> _segments=[];
        private readonly Dictionary<(int X,int Z),List<int>> _buckets=[];
        public BackgroundRoadIndex(IReadOnlyList<TerrainRoadCorridor> roads)
        {
            foreach(var road in roads)
            {
                if(!float.IsFinite(road.WidthMeters)||road.WidthMeters<=0)continue;
                for(var i=0;i<road.Points.Count-1;i++)
                {
                    var a=road.Points[i];var b=road.Points[i+1];var index=_segments.Count;_segments.Add((a,b,road.WidthMeters*.5f));
                    var pad=road.WidthMeters*.5;var minX=Bucket(Math.Min(a.X,b.X)-pad);var maxX=Bucket(Math.Max(a.X,b.X)+pad);var minZ=Bucket(Math.Min(a.Z,b.Z)-pad);var maxZ=Bucket(Math.Max(a.Z,b.Z)+pad);
                    for(var x=minX;x<=maxX;x++)for(var z=minZ;z<=maxZ;z++){if(!_buckets.TryGetValue((x,z),out var list))_buckets[(x,z)]=list=[];list.Add(index);}
                }
            }
        }
        public bool TryNearest(Vector3 p,float extraRadius,out float y,out float distance,out float halfWidth)
        {
            var radius=Math.Max(0,extraRadius)+20;var cx=Bucket(p.X);var cz=Bucket(p.Z);var range=(int)Math.Ceiling(radius/BucketSizeMeters);
            var best=float.PositiveInfinity;y=0;halfWidth=0;
            for(var x=cx-range;x<=cx+range;x++)for(var z=cz-range;z<=cz+range;z++)if(_buckets.TryGetValue((x,z),out var ids))foreach(var id in ids)
            {
                var s=_segments[id];var dx=s.B.X-s.A.X;var dz=s.B.Z-s.A.Z;var l2=dx*dx+dz*dz;var t=l2>1e-8f?Math.Clamp(((p.X-s.A.X)*dx+(p.Z-s.A.Z)*dz)/l2,0,1):0;
                var px=p.X-(s.A.X+dx*t);var pz=p.Z-(s.A.Z+dz*t);var d=px*px+pz*pz;
                if(d>=best)continue;best=d;y=s.A.Y+(s.B.Y-s.A.Y)*t;halfWidth=s.HalfWidth;
            }
            distance=float.IsFinite(best)?MathF.Sqrt(best):float.PositiveInfinity;return float.IsFinite(best);
        }
        private static int Bucket(double v)=>(int)Math.Floor(v/BucketSizeMeters);
    }

    public static TerrainMeshBuildResult Build(RouteTerrainData data, IReadOnlyList<VisualRouteSample> routeSamples)
        => Build(data, routeSamples, data.Chunks.Select(c => new TerrainZoneChunk(c, data.SpacingMeters, data.VerticalBiasMeters, true))
            .Concat(data.FarChunks.Select(c => new TerrainZoneChunk(c, data.FarSpacingMeters, data.FarVerticalBiasMeters, false))).ToArray());

    public static TerrainMeshBuildResult Build(RouteTerrainData data,IReadOnlyList<VisualRouteSample> routeSamples,
        IReadOnlyList<TerrainRoadCorridor> backgroundRoads)
        => Build(data,routeSamples,data.Chunks.Select(c=>new TerrainZoneChunk(c,data.SpacingMeters,data.VerticalBiasMeters,true))
            .Concat(data.FarChunks.Select(c=>new TerrainZoneChunk(c,data.FarSpacingMeters,data.FarVerticalBiasMeters,false))).ToArray(),backgroundRoads);

    public static TerrainMeshBuildResult Build(RouteTerrainData data, IReadOnlyList<VisualRouteSample> routeSamples,
        IReadOnlyList<TerrainZoneChunk> chunks)
        => Build(data,routeSamples,chunks,[]);

    public static TerrainMeshBuildResult Build(RouteTerrainData data,IReadOnlyList<VisualRouteSample> routeSamples,
        IReadOnlyList<TerrainZoneChunk> chunks,IReadOnlyList<TerrainRoadCorridor> backgroundRoads)
    {
        if (chunks.Count == 0 || routeSamples.Count < 2) return new([], 0, 0, 0);
        var routeIndex = new RouteSegmentSpatialIndex(routeSamples);
        var backgroundIndex=new BackgroundRoadIndex(backgroundRoads);
        var triangleGroups = new List<List<Triangle>>(chunks.Count);
        var normalSums = new Dictionary<GridKey, Vector3>();
        var uniqueVertices = new HashSet<GridKey>();
        var adjustedCount = 0;
        var triangleCount = 0;
        var roadCutoutCellCount = 0;
        foreach (var zone in chunks)
        {
            var chunk = zone.Chunk;
            var group = new List<Triangle>();
            var gridVertices = new Vector3?[(chunk.Columns + 1) * (chunk.Rows + 1)];
            var gridKeys = new GridKey[gridVertices.Length];
            for (var row = 0; row <= chunk.Rows; row++)
            for (var column = 0; column <= chunk.Columns; column++)
            {
                var index = row * (chunk.Columns + 1) + column;
                gridKeys[index] = Key(chunk.OriginX + column * zone.SpacingMeters,
                    chunk.OriginZ + row * zone.SpacingMeters, zone.SpacingMeters, zone.ApplyRoadClearance ? 0 : 1);
                if (chunk.ElevationsMeters[index] is not { } absoluteElevation) continue;
                var x = chunk.OriginX + column * zone.SpacingMeters;
                var z = chunk.OriginZ + row * zone.SpacingMeters;
                var visualY = (float)(absoluteElevation - data.RouteStartElevationMeters - zone.VerticalBiasMeters);
                var adjusted = zone.ApplyRoadClearance ? ApplyRoadClearance(new Vector3((float)x, visualY, (float)z),
                    routeIndex, data.RoadClearanceRadiusMeters, data.RoadClearanceMeters) : new Vector3((float)x, visualY, (float)z);
                if(zone.ApplyRoadClearance&&backgroundIndex.TryNearest(adjusted,(float)data.RoadClearanceMeters,out var streetY,out var streetDistance,out var halfWidth)
                    &&streetDistance<=halfWidth+data.RoadClearanceMeters)
                    adjusted=new Vector3(adjusted.X,Math.Min(adjusted.Y,streetY-(float)data.RoadClearanceMeters),adjusted.Z);
                if (adjusted.Y < visualY - 1e-5f) adjustedCount++;
                gridVertices[index] = adjusted;
            }

            for (var row = 0; row < chunk.Rows; row++)
            for (var column = 0; column < chunk.Columns; column++)
            {
                var cell = row * chunk.Columns + column;
                if (!chunk.CellMask[cell]) continue;
                if (zone.ApplyRoadClearance && CellIntersectsRoadSurface(chunk.OriginX + (column + 0.5) * zone.SpacingMeters,
                    chunk.OriginZ + (row + 0.5) * zone.SpacingMeters, zone.SpacingMeters, routeIndex,backgroundIndex))
                {
                    roadCutoutCellCount++;
                    continue;
                }
                var northwest = row * (chunk.Columns + 1) + column;
                var northeast = northwest + 1;
                var southwest = northwest + chunk.Columns + 1;
                var southeast = southwest + 1;
                if (gridVertices[northwest] is not { } a || gridVertices[northeast] is not { } b
                    || gridVertices[southwest] is not { } c || gridVertices[southeast] is not { } d) continue;
                Add(a, c, b, gridKeys[northwest], gridKeys[southwest], gridKeys[northeast]);
                Add(b, c, d, gridKeys[northeast], gridKeys[southwest], gridKeys[southeast]);
            }
            triangleCount += group.Count;
            triangleGroups.Add(group);

            void Add(Vector3 a, Vector3 b, Vector3 c, GridKey keyA, GridKey keyB, GridKey keyC)
            {
                var normal = Vector3.Cross(b - a, c - a);
                if (!float.IsFinite(normal.X + normal.Y + normal.Z) || normal.LengthSquared() < 1e-10f) return;
                if (normal.Y < 0)
                {
                    (b, c) = (c, b);
                    (keyB, keyC) = (keyC, keyB);
                    normal = -normal;
                }
                group.Add(new Triangle(a, b, c, keyA, keyB, keyC));
                normalSums[keyA] = normalSums.GetValueOrDefault(keyA) + normal;
                normalSums[keyB] = normalSums.GetValueOrDefault(keyB) + normal;
                normalSums[keyC] = normalSums.GetValueOrDefault(keyC) + normal;
                uniqueVertices.Add(keyA); uniqueVertices.Add(keyB); uniqueVertices.Add(keyC);
            }
        }

        var meshes = new TerrainChunkMeshData[triangleGroups.Count];
        for (var chunkIndex = 0; chunkIndex < triangleGroups.Count; chunkIndex++)
        {
            var triangles = triangleGroups[chunkIndex];
            var vertices = new Vector3[triangles.Count * 3];
            var normals = new Vector3[vertices.Length];
            var indices = new int[vertices.Length];
            for (var i = 0; i < triangles.Count; i++)
            {
                var triangle = triangles[i];
                Set(i * 3, triangle.A, triangle.KeyA);
                Set(i * 3 + 1, triangle.B, triangle.KeyB);
                Set(i * 3 + 2, triangle.C, triangle.KeyC);
            }
            meshes[chunkIndex] = new TerrainChunkMeshData(chunks[chunkIndex].Chunk.Id, vertices, normals, indices);

            void Set(int index, Vector3 vertex, GridKey key)
            {
                vertices[index] = vertex;
                var normal = normalSums.GetValueOrDefault(key);
                normals[index] = normal.LengthSquared() > 1e-10f ? Vector3.Normalize(normal) : Vector3.UnitY;
                indices[index] = index;
            }
        }
        return new(meshes, adjustedCount, triangleCount, uniqueVertices.Count, roadCutoutCellCount);
    }

    /// <summary>
    /// Removes whole near-terrain grid cells whose footprint can overlap the asphalt ribbon.
    /// Clearance applied to individual DEM vertices alone cannot prevent a coarse triangle from
    /// spanning over the road when all four cell corners happen to lie outside the clearance radius.
    /// </summary>
    private static bool CellIntersectsRoadSurface(double centerX, double centerZ, double spacing,
        RouteSegmentSpatialIndex routeIndex,BackgroundRoadIndex backgroundIndex)
    {
        var roadRadius = RoadRibbonMath.RoadWidthMeters * 0.5f * RoadRibbonMath.MaximumMiterScale;
        var conservativeRadius = roadRadius + spacing * Math.Sqrt(2) * 0.5;
        var center=new Vector3((float)centerX,0,(float)centerZ);
        return routeIndex.TryGetNearest(center, conservativeRadius, out _)
            || backgroundIndex.TryNearest(center,(float)(spacing*Math.Sqrt(2)*.5),out _,out var distance,out var halfWidth)
                &&distance<=halfWidth+spacing*(float)Math.Sqrt(2)*.5f;
    }

    public static Vector3 ApplyRoadClearance(Vector3 terrainVertex, IReadOnlyList<VisualRouteSample> routeSamples,
        double radiusMeters, double clearanceMeters)
    {
        if (radiusMeters <= 0 || clearanceMeters < 0) return terrainVertex;
        return ApplyRoadClearance(terrainVertex, new RouteSegmentSpatialIndex(routeSamples), radiusMeters, clearanceMeters);
    }

    private static Vector3 ApplyRoadClearance(Vector3 terrainVertex, RouteSegmentSpatialIndex routeIndex,
        double radiusMeters, double clearanceMeters)
    {
        if (radiusMeters <= 0 || clearanceMeters < 0) return terrainVertex;
        if (!routeIndex.TryGetNearest(terrainVertex, radiusMeters, out var roadY)) return terrainVertex;
        return new Vector3(terrainVertex.X,
            Math.Min(terrainVertex.Y, roadY - (float)clearanceMeters), terrainVertex.Z);
    }

    private static GridKey Key(double x, double z, double spacing, long zone = 0) =>
        new((long)Math.Round(x * 1000), (long)Math.Round(z * 1000), zone);
}
