using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using NVector3 = System.Numerics.Vector3;

/// <summary>Static visual-only road paint, combined into one mesh per 400 m route chunk.</summary>
public partial class RoadMarkingLayer : Node3D
{
    private const double ChunkLengthMeters = RoadMarkingGeometry.SpatialChunkLengthMeters;
    private const double VisibleDistanceMeters = 900;
    private const double NearbyDistanceMeters = 100;
    private sealed record Chunk(double CenterDistanceMeters, MeshInstance3D Mesh);
    private readonly List<Chunk> _chunks = [];
    private IReadOnlyList<double> _crossingDistances = [];
    private double _routeLengthMeters;

    public int CrossingSourcesConsidered { get; private set; }
    public int NearbyCrossingSources { get; private set; }
    public int ZebraCrossingsGenerated { get; private set; }
    public int CrossingDuplicatesSuppressed { get; private set; }
    public int TrafficSignalLinesGenerated { get; private set; }
    public int StopLinesGenerated { get; private set; }
    public int GiveWayLinesGenerated { get; private set; }
    public int MarkingTriangleCount { get; private set; }
    public int MeshInstanceCount => _chunks.Count;
    public int SceneNodeCount => 1 + MeshInstanceCount;
    public int LoadedMarkingCount => ZebraCrossingsGenerated + TrafficSignalLinesGenerated + StopLinesGenerated + GiveWayLinesGenerated;
    public int NearbyCrossingCount { get; private set; }
    public long BuildMilliseconds { get; private set; }

    public void Configure(VisualRoute route, RouteStreetFurnitureData data, RoadMarkingOptions? options = null)
    {
        if (_chunks.Count > 0 || LoadedMarkingCount > 0)
            throw new InvalidOperationException("Road-marking layer is already configured.");
        var timer = Stopwatch.StartNew();
        _routeLengthMeters = route.LengthMeters;
        var controls = data.Items.Select(item => new RoadMarkingControl(item.Category, item.OsmId,
            item.NearestRouteDistanceMeters, item.DistanceFromRouteMeters)).ToArray();
        var generated = RoadMarkingGeometry.Build(route, controls, ProceduralRoad.WidthMeters, options);
        CrossingSourcesConsidered = generated.CrossingSourcesConsidered;
        NearbyCrossingSources = generated.NearbyCrossingSources;
        ZebraCrossingsGenerated = generated.ZebraCrossings;
        CrossingDuplicatesSuppressed = generated.CrossingDuplicatesSuppressed;
        TrafficSignalLinesGenerated = generated.TrafficSignalLines;
        StopLinesGenerated = generated.StopLines;
        GiveWayLinesGenerated = generated.GiveWayLines;
        MarkingTriangleCount = generated.MarkingTriangleCount;
        _crossingDistances = generated.CrossingRouteDistancesMeters;

        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(.82f, .82f, .78f),
            Roughness = 1,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        var grouped = generated.Triangles.GroupBy(item => ChunkIndex(item.RouteDistanceMeters))
            .OrderBy(group => group.Key);
        foreach (var group in grouped)
        {
            var surface = new SurfaceTool();
            surface.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var triangle in group)
            {
                var a = ProceduralRoad.ToGodot(triangle.A);
                var b = ProceduralRoad.ToGodot(triangle.B);
                var c = ProceduralRoad.ToGodot(triangle.C);
                var normal = (b - a).Cross(c - a).Normalized();
                if (normal.Y < 0) normal = -normal;
                surface.SetNormal(normal);
                surface.AddVertex(a);
                surface.AddVertex(b);
                surface.AddVertex(c);
            }
            var mesh = surface.Commit();
            var instance = new MeshInstance3D { Name = "RoadMarkingMesh", Mesh = mesh, MaterialOverride = material };
            AddChild(instance);
            _chunks.Add(new Chunk(CenterDistance(group.Key), instance));
        }
        timer.Stop();
        BuildMilliseconds = timer.ElapsedMilliseconds;
    }

    public void UpdateForRideDistance(double rideDistanceMeters)
    {
        if (_routeLengthMeters <= 0) return;
        var wrapped = Wrap(rideDistanceMeters);
        NearbyCrossingCount = _crossingDistances.Count(distance => RouteDistance(distance, wrapped) <= NearbyDistanceMeters);
        foreach (var chunk in _chunks)
            chunk.Mesh.Visible = RouteDistance(chunk.CenterDistanceMeters, wrapped) <= VisibleDistanceMeters;
    }

    private int ChunkIndex(double distance) => Math.Clamp((int)(distance / ChunkLengthMeters), 0,
        Math.Max(0, (int)Math.Ceiling(_routeLengthMeters / ChunkLengthMeters) - 1));
    private double CenterDistance(int index) => Math.Min(_routeLengthMeters - .01, index * ChunkLengthMeters + ChunkLengthMeters / 2);
    private double Wrap(double distance) => ((_routeLengthMeters + distance % _routeLengthMeters) % _routeLengthMeters);
    private double RouteDistance(double first, double second)
    {
        var difference = Math.Abs(first - second);
        return Math.Min(difference, _routeLengthMeters - difference);
    }
}
