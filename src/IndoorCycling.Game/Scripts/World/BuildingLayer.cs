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
    public int FacadeTriangleCount { get; private set; }
    public int RoofTriangleCount { get; private set; }
    public int WindowTriangleCount { get; private set; }
    public int SurfaceCount { get; private set; }
    public int SharedMaterialCount { get; private set; }
    public int DetailedBuildingCount { get; private set; }
    public int RoofedBuildingCount { get; private set; }
    public int FlatRoofCount { get; private set; }
    public int GabledRoofCount { get; private set; }
    public int HippedRoofCount { get; private set; }
    public int FlatRoofFallbackCount { get; private set; }
    public int FootprintVertexCount { get; private set; }
    public double BuildMilliseconds { get; private set; }

    public void Configure(VisualRoute visualRoute, RouteDefinition route, IReadOnlyList<RouteBuildingData> buildings)
    {
        if (_chunks.Count > 0 || BuildingsLoaded > 0) throw new InvalidOperationException("Building layer is already configured.");
        var timer = Stopwatch.StartNew();
        _routeLengthMeters = route.DistanceMeters;
        var facadeMaterial = SharedMaterial();
        var roofMaterial = SharedMaterial();
        var windowMaterial = SharedMaterial();
        SharedMaterialCount = 3;
        var groups = buildings.GroupBy(item => Math.Clamp((int)(item.NearestRouteDistanceMeters / ChunkLengthMeters),
            0, Math.Max(0, (int)Math.Ceiling(_routeLengthMeters / ChunkLengthMeters) - 1))).OrderBy(group => group.Key);
        foreach (var group in groups)
        {
            var start = group.Key * ChunkLengthMeters;
            var centerDistance = Math.Min(_routeLengthMeters - .01, start + ChunkLengthMeters / 2);
            var origin = ProceduralRoad.ToGodot(visualRoute.GetPoseAtDistance(centerDistance).Position);
            var facadeVertices = new List<(System.Numerics.Vector3 Vertex, Color Colour)>();
            var roofVertices = new List<(System.Numerics.Vector3 Vertex, Color Colour)>();
            var windowVertices = new List<(System.Numerics.Vector3 Vertex, Color Colour)>();
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
                var tags = building.SourceTags ?? new Dictionary<string, string>();
                var category = building.VisualCategory == "generic" ? BuildingVisualStyle.Classify(tags) : building.VisualCategory;
                var variant = building.MaterialVariant is >= 0 and < 10
                    ? building.MaterialVariant : BuildingVisualStyle.MaterialVariant(building.OsmId, category);
                var facadeColour = ToGodotColour(BuildingVisualStyle.FacadeColor(variant,
                    tags.GetValueOrDefault("building:colour"), tags.GetValueOrDefault("building:material")));
                var roofColour = ToGodotColour(BuildingVisualStyle.RoofColor(variant,
                    tags.GetValueOrDefault("roof:colour"), tags.GetValueOrDefault("roof:material")));
                var rawRoofShape = tags.GetValueOrDefault("roof:shape");
                var footprintArea = FootprintArea(ring);
                var mappedOrInferredHeight = string.Equals(building.HeightSource, "Fallback", StringComparison.OrdinalIgnoreCase)
                    ? BuildingVisualStyle.ResolveFallbackVisualHeight(building.OsmId, category, footprintArea, (float)building.HeightMeters)
                    : (float)building.HeightMeters;
                var shape = BuildingVisualStyle.ResolveRoofShape(building.OsmId, rawRoofShape, category,
                    building.HeightSource, footprintArea, Math.Min(ring.Max(point => point.X) - ring.Min(point => point.X),
                        ring.Max(point => point.Z) - ring.Min(point => point.Z)));
                var footprintWidth = Math.Min(ring.Max(point => point.X) - ring.Min(point => point.X),
                    ring.Max(point => point.Z) - ring.Min(point => point.Z));
                var roofHeight = BuildingVisualStyle.ResolveRoofHeight(mappedOrInferredHeight, footprintWidth,
                    tags.GetValueOrDefault("roof:height"), tags.GetValueOrDefault("roof:levels"));
                var storeys = BuildingVisualStyle.ResolveStoreys(tags.GetValueOrDefault("building:levels"), mappedOrInferredHeight);
                BuildingMeshGeometry.StyledMesh geometry;
                try { geometry = BuildingMeshGeometry.BuildStyled(ring, mappedOrInferredHeight,
                    shape, roofHeight, storeys, BuildingVisualStyle.ShouldAddFacadeDetail(building.DistanceFromRouteMeters),
                    facadePattern: BuildingVisualStyle.FacadePattern(building.OsmId)); }
                catch (ArgumentException) { continue; }
                Add(facadeVertices, geometry.Facade, facadeColour);
                Add(roofVertices, geometry.Roof, roofColour);
                // Keep glazing visually dark, but lift it above black so shaded
                // facades retain a little readable blue-grey surface response.
                Add(windowVertices, geometry.Windows, new Color(.18f,.23f,.27f));
                FacadeTriangleCount += geometry.FacadeTriangleCount;
                RoofTriangleCount += geometry.RoofTriangleCount;
                WindowTriangleCount += geometry.WindowTriangleCount;
                TriangleCount += geometry.FacadeTriangleCount + geometry.RoofTriangleCount + geometry.WindowTriangleCount;
                if (geometry.WindowCount > 0) DetailedBuildingCount++;
                if (geometry.RoofTriangleCount > 0) RoofedBuildingCount++;
                if (geometry.EffectiveRoofShape == "gabled") GabledRoofCount++;
                else if (geometry.EffectiveRoofShape == "hipped") HippedRoofCount++;
                else
                {
                    FlatRoofCount++;
                    if (!string.IsNullOrWhiteSpace(rawRoofShape)
                        && !rawRoofShape.Equals("flat", StringComparison.OrdinalIgnoreCase)) FlatRoofFallbackCount++;
                }
                FootprintVertexCount += building.Footprint.Count;
                buildingCount++;
            }
            if (buildingCount == 0) continue;
            var mesh = new ArrayMesh();
            AddSurface(mesh, facadeVertices, facadeMaterial);
            AddSurface(mesh, roofVertices, roofMaterial);
            AddSurface(mesh, windowVertices, windowMaterial);
            SurfaceCount += mesh.GetSurfaceCount();
            var instance = new MeshInstance3D
            {
                Name = $"BuildingsChunk{group.Key:D2}", Position = origin, Mesh = mesh,
            };
            AddChild(instance);
            _chunks.Add(new Chunk(group.Key, centerDistance, instance));
            BuildingsLoaded += buildingCount;
        }
        timer.Stop(); BuildMilliseconds = timer.Elapsed.TotalMilliseconds;
        GD.Print($"Buildings visual: loaded={BuildingsLoaded} detailed={DetailedBuildingCount} " +
            $"pitchedRoofs={RoofedBuildingCount} triangles={TriangleCount} " +
            $"facade={FacadeTriangleCount} roof={RoofTriangleCount} windows={WindowTriangleCount} " +
            $"chunks={ChunkCount} meshInstances={MeshInstanceCount} surfaces={SurfaceCount} " +
            $"sharedMaterials={SharedMaterialCount} buildMs={BuildMilliseconds:F1}");
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

    private static StandardMaterial3D SharedMaterial() => new()
    {
        AlbedoColor = Colors.White, VertexColorUseAsAlbedo = true, Roughness = 0.92f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled
    };

    private static Color ToGodotColour(BuildingRgb colour) => new(colour.R, colour.G, colour.B);

    private static float FootprintArea(IReadOnlyList<System.Numerics.Vector3> closedRing)
    {
        double twiceArea = 0;
        for (var index = 0; index < closedRing.Count - 1; index++)
        {
            var current = closedRing[index]; var next = closedRing[index + 1];
            twiceArea += current.X * next.Z - next.X * current.Z;
        }
        return (float)(Math.Abs(twiceArea) * .5);
    }

    private static void Add(List<(System.Numerics.Vector3 Vertex, Color Colour)> target,
        IReadOnlyList<System.Numerics.Vector3> vertices, Color colour)
    {
        foreach (var vertex in vertices) target.Add((vertex, colour));
    }

    private static void AddSurface(ArrayMesh mesh, IReadOnlyList<(System.Numerics.Vector3 Vertex, Color Colour)> vertices,
        StandardMaterial3D material)
    {
        if (vertices.Count == 0) return;
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        surface.SetMaterial(material);
        foreach (var (vertex, colour) in vertices)
        {
            surface.SetColor(colour);
            surface.AddVertex(new Vector3(vertex.X, vertex.Y, vertex.Z));
        }
        surface.GenerateNormals();
        surface.Commit(mesh);
    }
}
