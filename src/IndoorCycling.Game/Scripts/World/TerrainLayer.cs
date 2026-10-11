using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;

/// <summary>Offline LINZ terrain chunks, culled by physical distance from the rider.</summary>
public partial class TerrainLayer : Node3D
{
    private const double VisibleRouteRadiusMeters = 1200;
    private VisualRoute? _route;
    private readonly System.Collections.Generic.List<(Vector2 Center, bool Far, MeshInstance3D Mesh)> _chunks = [];
    private double _routeLength;
    public int TerrainChunksLoaded => _chunks.Count;
    public int VisibleTerrainChunks { get; private set; }
    public double TerrainSpacingMeters { get; private set; }
    public double FarTerrainSpacingMeters { get; private set; }
    public int NearTerrainChunks => _chunks.Count(c => !c.Far);
    public int FarTerrainChunks => _chunks.Count(c => c.Far);
    public int VisibleNearTerrainChunks { get; private set; }
    public int VisibleFarTerrainChunks { get; private set; }
    public int TerrainTriangleCount { get; private set; }
    public int RoadClearanceAdjustedVertices { get; private set; }
    public double BuildMilliseconds { get; private set; }

    public bool Configure(VisualRoute route, string resourceName, string routeId,
        IReadOnlyList<TerrainRoadCorridor>? backgroundRoads = null)
    {
        var data = RouteTerrainDataLoader.TryLoadEmbedded(typeof(RouteCatalog).Assembly, resourceName, routeId);
        if (data is null) return false;
        var timer = Stopwatch.StartNew();
        _routeLength = route.LengthMeters;
        _route = route;
        TerrainSpacingMeters = data.SpacingMeters;
        FarTerrainSpacingMeters = data.FarSpacingMeters;
        var built = backgroundRoads is { Count: > 0 }
            ? TerrainMeshGeometry.Build(data, route.Samples, backgroundRoads)
            : TerrainMeshGeometry.Build(data, route.Samples);
        RoadClearanceAdjustedVertices = built.AdjustedForRoadClearanceVertices;
        TerrainTriangleCount = built.TriangleCount;
        var material = new StandardMaterial3D
        {
            AlbedoColor = Colors.White, VertexColorUseAsAlbedo = true, Roughness = 1,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        foreach (var chunk in built.Chunks)
        {
            if (chunk.Indices.Length == 0) continue;
            var surface = new SurfaceTool();
            surface.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var index in chunk.Indices)
            {
                var faceStart = index - index % 3;
                var a = ToGodot(chunk.Vertices[faceStart]);
                var b = ToGodot(chunk.Vertices[faceStart + 1]);
                var c = ToGodot(chunk.Vertices[faceStart + 2]);
                var normal = (b - a).Cross(c - a).Normalized();
                if (normal.Y < 0) normal = -normal;
                var vertex = ToGodot(chunk.Vertices[index]);
                var variation = StableVariation((a + b + c) / 3);
                surface.SetNormal(normal);
                surface.SetColor(WodraWorldStyle.TerrainFaceColor(vertex.Y, normal.Y, variation));
                surface.AddVertex(vertex);
            }
            surface.Index();
            var instance = new MeshInstance3D
            {
                Name = chunk.Id, Mesh = surface.Commit(), MaterialOverride = material
            };
            AddChild(instance);
            var far = chunk.Id.StartsWith("far-", StringComparison.Ordinal);
            var center = new Vector2(chunk.Vertices.Average(v => v.X), chunk.Vertices.Average(v => v.Z));
            _chunks.Add((center, far, instance));
        }
        timer.Stop();
        BuildMilliseconds = timer.Elapsed.TotalMilliseconds;
        return _chunks.Count > 0;
    }

    public void UpdateForRideDistance(double distance)
    {
        if (_routeLength <= 0 || _route is null) return;
        var rider = _route.GetPoseAtDistance(distance).Position;
        VisibleTerrainChunks = 0;
        VisibleNearTerrainChunks = VisibleFarTerrainChunks = 0;
        foreach (var chunk in _chunks)
        {
            var dx = chunk.Center.X - rider.X;
            var dz = chunk.Center.Y - rider.Z;
            var visible = dx * dx + dz * dz <= VisibleRouteRadiusMeters * VisibleRouteRadiusMeters;
            chunk.Mesh.Visible = visible;
            if (visible) { VisibleTerrainChunks++; if (chunk.Far) VisibleFarTerrainChunks++; else VisibleNearTerrainChunks++; }
        }
    }

    private static Vector3 ToGodot(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);
    private static float StableVariation(Vector3 position)
    {
        var hash = Math.Sin(position.X * 0.071 + position.Z * 0.113) * 43758.5453;
        return (float)(hash - Math.Floor(hash));
    }
}
