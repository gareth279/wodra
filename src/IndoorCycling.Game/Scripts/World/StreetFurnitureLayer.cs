using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;

/// <summary>Static OSM roadside object proxies batched into category/route-chunk MultiMeshes.</summary>
public partial class StreetFurnitureLayer : Node3D
{
    private const double ChunkLengthMeters = 400;
    private const double VisibleDistanceMeters = 900;
    private const double NearbyDistanceMeters = 100;
    private sealed record Chunk(double CenterDistanceMeters, IReadOnlyList<Node3D> Nodes);
    private readonly List<Chunk> _chunks = [];
    private IReadOnlyList<RouteStreetFurnitureItemData> _items = [];
    private double _routeLengthMeters;

    public int LoadedCount { get; private set; }
    public int NearbyCount { get; private set; }
    public int MultiMeshCount { get; private set; }
    public int SceneNodeCount => 1 + _chunks.Sum(chunk => chunk.Nodes.Count);
    public double BuildMilliseconds { get; private set; }

    public void Configure(VisualRoute visualRoute, RouteDefinition route, RouteStreetFurnitureData data)
    {
        if (_chunks.Count > 0 || LoadedCount > 0) throw new InvalidOperationException("Street-furniture layer is already configured.");
        var timer = Stopwatch.StartNew();
        _routeLengthMeters = route.DistanceMeters;
        _items = data.Items;
        LoadedCount = data.Items.Count;
        var nodesByChunk = new Dictionary<int, List<Node3D>>();
        var centers = new Dictionary<int, double>();
        var chunks = data.Items.GroupBy(item => ChunkIndex(item.NearestRouteDistanceMeters)).OrderBy(group => group.Key);
        foreach (var chunk in chunks)
        {
            var nodes = new List<Node3D>();
            foreach (var category in chunk.GroupBy(item => item.Category, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var items = category.ToArray();
                var parts = Parts(category.Key);
                foreach (var part in parts)
                {
                    var transforms = items.Select(item =>
                    {
                        var elevation = item.ElevationMeters!.Value;
                        var mapped = visualRoute.GetPositionForCoordinate(item.Latitude, item.Longitude, elevation);
                        var position = ProceduralRoad.ToGodot(mapped);
                        var degrees = item.DirectionDegrees ?? item.RouteTangentDegrees;
                        var rotation = -degrees * Math.PI / 180d;
                        var basis = new Basis(Vector3.Up, (float)rotation).Scaled(part.Scale);
                        var offset = basis * part.LocalOffset;
                        return new Transform3D(basis, position + offset);
                    }).ToArray();
                    var instance = CreateMultiMesh($"{category.Key}-{part.Name}", part.Mesh, part.Material, transforms);
                    AddChild(instance); nodes.Add(instance); MultiMeshCount++;
                }
            }
            nodesByChunk[chunk.Key] = nodes;
            centers[chunk.Key] = CenterDistance(chunk.Key);
        }
        foreach (var group in nodesByChunk.OrderBy(item => item.Key))
            _chunks.Add(new Chunk(centers[group.Key], group.Value));
        timer.Stop(); BuildMilliseconds = timer.Elapsed.TotalMilliseconds;
    }

    public void UpdateForRideDistance(double rideDistanceMeters)
    {
        if (_routeLengthMeters <= 0) return;
        var wrapped = Wrap(rideDistanceMeters);
        NearbyCount = _items.Count(item => RouteDistance(item.NearestRouteDistanceMeters, wrapped) <= NearbyDistanceMeters);
        foreach (var chunk in _chunks)
        {
            var visible = RouteDistance(chunk.CenterDistanceMeters, wrapped) <= VisibleDistanceMeters;
            foreach (var node in chunk.Nodes) node.Visible = visible;
        }
    }

    private int ChunkIndex(double distance) => Math.Clamp((int)(distance / ChunkLengthMeters), 0,
        Math.Max(0, (int)Math.Ceiling(_routeLengthMeters / ChunkLengthMeters) - 1));
    private double CenterDistance(int index) => Math.Min(_routeLengthMeters - .01, index * ChunkLengthMeters + ChunkLengthMeters / 2);
    private double Wrap(double distance) => ((_routeLengthMeters + distance % _routeLengthMeters) % _routeLengthMeters);
    private double RouteDistance(double a, double b)
    {
        var difference = Math.Abs(a - b);
        return Math.Min(difference, _routeLengthMeters - difference);
    }

    private sealed record Part(string Name, Mesh Mesh, Material Material, Vector3 Scale, Vector3 LocalOffset);
    private static IReadOnlyList<Part> Parts(string category)
    {
        var pole = new StandardMaterial3D { AlbedoColor = new Color(.24f,.25f,.24f), Roughness = 1 };
        var sign = new StandardMaterial3D { AlbedoColor = category is "stop-sign" or "traffic-signal" ? new Color(.78f,.12f,.1f) : new Color(.9f,.8f,.48f), Roughness = 1 };
        var wood = new StandardMaterial3D { AlbedoColor = new Color(.43f,.28f,.15f), Roughness = 1 };
        var gray = new StandardMaterial3D { AlbedoColor = new Color(.34f,.36f,.37f), Roughness = 1 };
        Part Cylinder(string name, Material material, Vector3 scale, float y) =>
            new(name, new CylinderMesh { TopRadius = .5f, BottomRadius = .5f, Height = 1, RadialSegments = 6 }, material, scale, new Vector3(0,y,0));
        Part Box(string name, Material material, Vector3 scale, Vector3 offset) =>
            new(name, new BoxMesh { Size = Vector3.One }, material, scale, offset);
        return category switch
        {
            "street-lamp" => [Cylinder("pole",pole,new(.12f,6,.12f),3), Box("lamp-head",sign,new(.8f,.12f,.2f),new(0,5.9f,-.28f))],
            "traffic-signal" => [Cylinder("pole",pole,new(.14f,4,.14f),2), Box("signal",sign,new(.38f,1.15f,.32f),new(0,3.9f,0))],
            "stop-sign" or "give-way-sign" or "traffic-sign" => [Cylinder("pole",pole,new(.1f,2.3f,.1f),1.15f), Box("sign",sign,new(.72f,.72f,.09f),new(0,2.15f,0))],
            "bollard" => [Cylinder("bollard",sign,new(.26f,.85f,.26f),.425f)],
            "crossing" => [Cylinder("crossing-post",pole,new(.13f,1.8f,.13f),.9f), Box("crossing-marker",sign,new(.46f,.38f,.12f),new(0,1.65f,0))],
            "bus-stop" => [Cylinder("stop-pole",pole,new(.12f,2.7f,.12f),1.35f), Box("stop-plate",sign,new(.5f,.62f,.1f),new(0,2.45f,0))],
            "bench" => [Box("seat",wood,new(1.25f,.14f,.45f),new(0,.48f,0)), Box("back",wood,new(1.25f,.52f,.12f),new(0,.78f,.17f)),
                Box("left-leg",gray,new(.09f,.44f,.09f),new(-.48f,.22f,0)), Box("right-leg",gray,new(.09f,.44f,.09f),new(.48f,.22f,0))],
            _ => [Cylinder("marker",gray,new(.16f,1,.16f),.5f)]
        };
    }

    private static MultiMeshInstance3D CreateMultiMesh(string name, Mesh mesh, Material material, IReadOnlyList<Transform3D> transforms)
    {
        var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = transforms.Count };
        for (var i = 0; i < transforms.Count; i++) multi.SetInstanceTransform(i, transforms[i]);
        return new MultiMeshInstance3D { Name = name, Multimesh = multi, MaterialOverride = material };
    }
}
