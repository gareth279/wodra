using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

public sealed record TerrainChunkMeshData(string Id, Vector3[] Vertices, Vector3[] Normals, int[] Indices);
public sealed record TerrainZoneChunk(RouteTerrainChunkData Chunk, double SpacingMeters, double VerticalBiasMeters, bool ApplyRoadClearance);
public sealed record TerrainMeshBuildResult(TerrainChunkMeshData[] Chunks, int AdjustedForRoadClearanceVertices,
    int TriangleCount, int UniqueVertexCount);

/// <summary>Godot-independent terrain mesh builder used by runtime and focused geometry tests.</summary>
public static class TerrainMeshGeometry
{
    private readonly record struct GridKey(long X, long Z, long Zone);
    private sealed record Triangle(Vector3 A, Vector3 B, Vector3 C, GridKey KeyA, GridKey KeyB, GridKey KeyC);

    public static TerrainMeshBuildResult Build(RouteTerrainData data, IReadOnlyList<VisualRouteSample> routeSamples)
        => Build(data, routeSamples, data.Chunks.Select(c => new TerrainZoneChunk(c, data.SpacingMeters, data.VerticalBiasMeters, true))
            .Concat(data.FarChunks.Select(c => new TerrainZoneChunk(c, data.FarSpacingMeters, data.FarVerticalBiasMeters, false))).ToArray());

    public static TerrainMeshBuildResult Build(RouteTerrainData data, IReadOnlyList<VisualRouteSample> routeSamples,
        IReadOnlyList<TerrainZoneChunk> chunks)
    {
        if (chunks.Count == 0 || routeSamples.Count < 2) return new([], 0, 0, 0);
        var triangleGroups = new List<List<Triangle>>(chunks.Count);
        var normalSums = new Dictionary<GridKey, Vector3>();
        var uniqueVertices = new HashSet<GridKey>();
        var adjustedCount = 0;
        var triangleCount = 0;
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
                var adjusted = zone.ApplyRoadClearance ? ApplyRoadClearance(new Vector3((float)x, visualY, (float)z), routeSamples,
                    data.RoadClearanceRadiusMeters, data.RoadClearanceMeters) : new Vector3((float)x, visualY, (float)z);
                if (adjusted.Y < visualY - 1e-5f) adjustedCount++;
                gridVertices[index] = adjusted;
            }

            for (var row = 0; row < chunk.Rows; row++)
            for (var column = 0; column < chunk.Columns; column++)
            {
                var cell = row * chunk.Columns + column;
                if (!chunk.CellMask[cell]) continue;
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
        return new(meshes, adjustedCount, triangleCount, uniqueVertices.Count);
    }

    public static Vector3 ApplyRoadClearance(Vector3 terrainVertex, IReadOnlyList<VisualRouteSample> routeSamples,
        double radiusMeters, double clearanceMeters)
    {
        if (radiusMeters <= 0 || clearanceMeters < 0) return terrainVertex;
        var nearestDistanceSquared = double.PositiveInfinity;
        var roadY = 0f;
        for (var i = 0; i < routeSamples.Count - 1; i++)
        {
            var a = routeSamples[i].Position;
            var b = routeSamples[i + 1].Position;
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var lengthSquared = dx * dx + dz * dz;
            var t = lengthSquared > 1e-8f
                ? Math.Clamp(((terrainVertex.X - a.X) * dx + (terrainVertex.Z - a.Z) * dz) / lengthSquared, 0, 1)
                : 0;
            var nearestX = a.X + dx * t;
            var nearestZ = a.Z + dz * t;
            var distanceSquared = (terrainVertex.X - nearestX) * (terrainVertex.X - nearestX)
                + (terrainVertex.Z - nearestZ) * (terrainVertex.Z - nearestZ);
            if (distanceSquared < nearestDistanceSquared)
            {
                nearestDistanceSquared = distanceSquared;
                roadY = a.Y + (b.Y - a.Y) * t;
            }
        }
        if (nearestDistanceSquared > radiusMeters * radiusMeters) return terrainVertex;
        return new Vector3(terrainVertex.X,
            Math.Min(terrainVertex.Y, roadY - (float)clearanceMeters), terrainVertex.Z);
    }

    private static GridKey Key(double x, double z, double spacing, long zone = 0) =>
        new((long)Math.Round(x * 1000), (long)Math.Round(z * 1000), zone);
}
