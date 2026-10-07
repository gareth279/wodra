using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;

/// <summary>Static, batched OSM footprint extrusions in the canonical route-local world frame.</summary>
public partial class BuildingLayer : Node3D
{
    private const double ChunkLengthMeters = 350;
    private const double VisibleDistanceMeters = 950;
    private sealed record Chunk(int Index, double CenterDistanceMeters, MeshInstance3D Instance);
    private readonly List<Chunk> _chunks = [];
    private double _routeLengthMeters;

    public int BuildingsLoaded { get; private set; }
    public int ChunkCount => _chunks.Count;
    public int MeshInstanceCount => _chunks.Count;
    public int VisibleChunkCount => _chunks.Count(item => item.Instance.Visible);
    public int TriangleCount { get; private set; }
    public int FootprintVertexCount { get; private set; }
    public double BuildMilliseconds { get; private set; }

    public void Configure(VisualRoute visualRoute, RouteDefinition route, IReadOnlyList<RouteBuildingData> buildings)
    {
        if (_chunks.Count > 0 || BuildingsLoaded > 0) throw new InvalidOperationException("Building layer is already configured.");
        var timer = Stopwatch.StartNew();
        _routeLengthMeters = route.DistanceMeters;
        var groups = buildings.GroupBy(item => Math.Clamp((int)(item.NearestRouteDistanceMeters / ChunkLengthMeters),
            0, Math.Max(0, (int)Math.Ceiling(_routeLengthMeters / ChunkLengthMeters) - 1))).OrderBy(group => group.Key);
        foreach (var group in groups)
        {
            var start = group.Key * ChunkLengthMeters;
            var centerDistance = Math.Min(_routeLengthMeters - .01, start + ChunkLengthMeters / 2);
            var origin = ProceduralRoad.ToGodot(visualRoute.GetPoseAtDistance(centerDistance).Position);
            var surface = new SurfaceTool(); surface.Begin(Mesh.PrimitiveType.Triangles);
            var buildingCount = 0;
            foreach (var building in group)
            {
                if (building.BaseElevationMeters is not { } elevation || building.Footprint.Count < 4) continue;
                var ring = building.Footprint.Select(point =>
                {
                    var local = visualRoute.GetPositionForCoordinate(point.Latitude, point.Longitude, elevation);
                    var world = ProceduralRoad.ToGodot(local);
                    return new System.Numerics.Vector3(world.X - origin.X, world.Y - origin.Y, world.Z - origin.Z);
                }).ToArray();
                System.Numerics.Vector3[] vertices;
                try { vertices = BuildingMeshGeometry.Extrude(ring, (float)building.HeightMeters); }
                catch (ArgumentException) { continue; }
                foreach (var vertex in vertices)
                {
                    surface.SetColor(Colour(building));
                    surface.AddVertex(new Vector3(vertex.X, vertex.Y, vertex.Z));
                }
                TriangleCount += vertices.Length / 3;
                FootprintVertexCount += building.Footprint.Count;
                buildingCount++;
            }
            if (buildingCount == 0) continue;
            surface.GenerateNormals();
            var mesh = surface.Commit();
            var instance = new MeshInstance3D
            {
                Name = $"BuildingsChunk{group.Key:D2}", Position = origin, Mesh = mesh,
                MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.White, VertexColorUseAsAlbedo = true, Roughness = 1 }
            };
            AddChild(instance);
            _chunks.Add(new Chunk(group.Key, centerDistance, instance));
            BuildingsLoaded += buildingCount;
        }
        timer.Stop(); BuildMilliseconds = timer.Elapsed.TotalMilliseconds;
    }

    public void UpdateForRideDistance(double distanceMeters)
    {
        if (_routeLengthMeters <= 0) return;
        var wrapped = ((_routeLengthMeters + distanceMeters % _routeLengthMeters) % _routeLengthMeters);
        foreach (var chunk in _chunks)
        {
            var difference = Math.Abs(chunk.CenterDistanceMeters - wrapped);
            var along = Math.Min(difference, _routeLengthMeters - difference);
            chunk.Instance.Visible = along <= VisibleDistanceMeters;
        }
    }

    private static Color Colour(RouteBuildingData building)
    {
        if (building.SourceTags.ContainsKey("historic")) return new Color(.63f, .49f, .36f);
        return building.BuildingType switch
        {
            "industrial" or "warehouse" => new Color(.46f, .49f, .51f),
            "university" or "civic" or "public" => new Color(.69f, .61f, .48f),
            _ => new Color(.63f, .57f, .50f)
        };
    }
}
