using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;

/// <summary>Static OSM green-area meshes and compact MultiMesh tree batches.</summary>
public partial class VegetationLayer : Node3D
{
    private const double ChunkLengthMeters = 400;
    private const double VisibleDistanceMeters = 1000;
    private sealed record VisibleChunk(int Index, double CenterDistanceMeters, List<Node3D> Nodes);
    private readonly List<VisibleChunk> _chunks = [];
    private double _routeLengthMeters;

    public int VegetationAreaCount { get; private set; }
    public int TreesLoaded { get; private set; }
    public int ExplicitTrees { get; private set; }
    public int GeneratedTrees { get; private set; }
    public int TreeRows { get; private set; }
    public int VisibleVegetationChunks { get; private set; }
    public int AreaTriangleCount { get; private set; }
    public int MultiMeshCount { get; private set; }
    public int AreaMeshInstanceCount { get; private set; }
    public int CreatedSceneNodeCount => _chunks.Sum(item => item.Nodes.Count) + 1;
    public double BuildMilliseconds { get; private set; }

    public void Configure(VisualRoute visualRoute, RouteDefinition route, RouteVegetationData data)
    {
        if (_chunks.Count > 0 || VegetationAreaCount > 0 || TreesLoaded > 0)
            throw new InvalidOperationException("Vegetation layer is already configured.");
        var timer = Stopwatch.StartNew();
        _routeLengthMeters = route.DistanceMeters;
        var originElevation = route.Points?.FirstOrDefault()?.ElevationMeters ?? 0;
        VegetationAreaCount = data.Areas.Count;
        TreesLoaded = data.Trees.Count;
        ExplicitTrees = data.Trees.Count(item => item.Source == "osm-tree");
        TreeRows = data.Trees.Count(item => item.Source == "tree-row");
        GeneratedTrees = data.Trees.Count(item => item.Source == "generated");
        var chunkNodes = new Dictionary<int, List<Node3D>>();
        var chunkCenters = new Dictionary<int, double>();
        var areaGroups = data.Areas.Where(item => item.RenderGround && item.BaseElevationMeters is not null)
            .GroupBy(item => Chunk(item.NearestRouteDistanceMeters)).OrderBy(group => group.Key);
        foreach (var group in areaGroups)
        {
            var centerDistance = CenterDistance(group.Key);
            var surface = new SurfaceTool(); surface.Begin(Mesh.PrimitiveType.Triangles);
            var triangles = 0;
            foreach (var area in group)
            {
                var ring = area.Polygon.Select(coordinate =>
                {
                    var local = visualRoute.GetPositionForCoordinate(coordinate[1], coordinate[0], area.BaseElevationMeters!.Value);
                    return new System.Numerics.Vector3(local.X, (float)(local.Y + .035), local.Z);
                }).ToArray();
                System.Numerics.Vector3[] polygonTriangles;
                try { polygonTriangles = BuildingMeshGeometry.TriangulateSurface(ring); }
                catch (ArgumentException) { continue; }
                var color = AreaColor(area.Category);
                foreach (var point in polygonTriangles)
                {
                    surface.SetColor(color);
                    surface.AddVertex(new Vector3(point.X, point.Y, point.Z));
                }
                triangles += polygonTriangles.Length / 3;
            }
            if (triangles == 0) continue;
            surface.GenerateNormals();
            var instance = new MeshInstance3D
            {
                Name = $"VegetationAreaChunk{group.Key:D2}", Mesh = surface.Commit(),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.White, VertexColorUseAsAlbedo = true, Roughness = 0.96f }
            };
            AddChild(instance); AddNode(chunkNodes, group.Key, instance); chunkCenters[group.Key] = centerDistance;
            AreaTriangleCount += triangles; AreaMeshInstanceCount++;
        }

        var treeGroups = data.Trees.GroupBy(item => Chunk(item.NearestRouteDistanceMeters)).OrderBy(group => group.Key);
        foreach (var group in treeGroups)
        {
            var transforms = group.Select(tree => (Tree: tree, Position: ToGodot(visualRoute.GetPositionForCoordinate(
                tree.Latitude, tree.Longitude, tree.ElevationMeters ?? originElevation)))).ToArray();
            var nodes = new List<Node3D>();
            var trunkTransforms = transforms.Select(item => ScaledTransform(item.Position + Vector3.Up * (float)(item.Tree.HeightMeters * .15),
                new Vector3(1, (float)(item.Tree.HeightMeters * .3), 1))).ToArray();
            nodes.Add(CreateMultiMesh("TreeTrunks", new CylinderMesh
            { TopRadius = .12f, BottomRadius = .19f, Height = 1, RadialSegments = 5 },
                new StandardMaterial3D { AlbedoColor = new Color(.31f, .2f, .12f), Roughness = 0.9f }, trunkTransforms));

            foreach (var type in new[] { "conifer", "broadleaf" })
            {
                // Explicit OSM points and tree rows carry the generic type. Give
                // them the broadleaf proxy rather than rendering bare trunks.
                var selected = transforms.Where(item => type == "broadleaf"
                    ? item.Tree.TreeType != "conifer"
                    : item.Tree.TreeType == type).ToArray();
                if (selected.Length == 0) continue;
                var canopy = type == "conifer"
                    ? (Mesh)new CylinderMesh { TopRadius = 0, BottomRadius = 1, Height = 1.6f, RadialSegments = 6 }
                    : new SphereMesh { Radius = 1, Height = 1.5f, RadialSegments = 6, Rings = 3 };
                var canopyTransforms = selected.Select(item =>
                {
                    var tree = item.Tree; var radius = (float)tree.CanopyRadiusMeters;
                    var canopyHeight = type == "conifer" ? (float)(tree.HeightMeters * .43) : (float)(tree.HeightMeters * .32);
                    var centerY = (float)(tree.HeightMeters * (type == "conifer" ? .70 : .72));
                    return ScaledTransform(item.Position + Vector3.Up * centerY,
                        new Vector3(radius, canopyHeight, radius));
                }).ToArray();
                nodes.Add(CreateMultiMesh(type == "conifer" ? "ConiferCanopies" : "BroadleafCanopies", canopy,
                    new StandardMaterial3D { AlbedoColor = type == "conifer" ? new Color(.14f, .34f, .18f) : new Color(.2f, .43f, .19f), Roughness = 0.96f }, canopyTransforms));
            }
            foreach (var node in nodes) AddChild(node);
            MultiMeshCount += nodes.Count;
            chunkNodes[group.Key] = chunkNodes.GetValueOrDefault(group.Key, []);
            chunkNodes[group.Key].AddRange(nodes);
            chunkCenters[group.Key] = CenterDistance(group.Key);
        }
        foreach (var pair in chunkNodes.OrderBy(item => item.Key))
            _chunks.Add(new VisibleChunk(pair.Key, chunkCenters[pair.Key], pair.Value));
        timer.Stop(); BuildMilliseconds = timer.Elapsed.TotalMilliseconds;
    }

    public void UpdateForRideDistance(double rideDistanceMeters)
    {
        if (_routeLengthMeters <= 0) return;
        var wrapped = ((_routeLengthMeters + rideDistanceMeters % _routeLengthMeters) % _routeLengthMeters);
        var visible = 0;
        foreach (var chunk in _chunks)
        {
            var difference = Math.Abs(chunk.CenterDistanceMeters - wrapped);
            var along = Math.Min(difference, _routeLengthMeters - difference);
            var show = along <= VisibleDistanceMeters;
            foreach (var node in chunk.Nodes) node.Visible = show;
            if (show) visible++;
        }
        VisibleVegetationChunks = visible;
    }

    private int Chunk(double routeDistance) => Math.Clamp((int)(routeDistance / ChunkLengthMeters), 0,
        Math.Max(0, (int)Math.Ceiling(_routeLengthMeters / ChunkLengthMeters) - 1));
    private double CenterDistance(int index) => Math.Min(_routeLengthMeters - .01, index * ChunkLengthMeters + ChunkLengthMeters / 2);
    private void AddNode(Dictionary<int, List<Node3D>> groups, int index, Node3D node)
    {
        groups[index] = groups.GetValueOrDefault(index, []); groups[index].Add(node);
    }
    private static Color AreaColor(string category) => category switch
    {
        "wood" or "forest" or "nature-reserve" => new Color(.16f, .3f, .17f),
        "grass" or "grassland" or "meadow" => new Color(.32f, .43f, .23f),
        _ => new Color(.2f, .37f, .24f)
    };
    private static Vector3 ToGodot(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);
    private static Transform3D ScaledTransform(Vector3 position, Vector3 scale) =>
        new(new Basis(Vector3.Right * scale.X, Vector3.Up * scale.Y, Vector3.Back * scale.Z), position);
    private static MultiMeshInstance3D CreateMultiMesh(string name, Mesh mesh, Material material,
        IReadOnlyList<Transform3D> transforms)
    {
        var multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = transforms.Count
        };
        for (var i = 0; i < transforms.Count; i++) multimesh.SetInstanceTransform(i, transforms[i]);
        return new MultiMeshInstance3D { Name = name, Multimesh = multimesh, MaterialOverride = material };
    }
}
