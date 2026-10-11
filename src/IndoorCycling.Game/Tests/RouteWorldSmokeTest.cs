using System;
using System.Diagnostics;
using Godot;

/// <summary>Headless Godot smoke coverage for the real built-in and installed Dunedin world packages.</summary>
public partial class RouteWorldSmokeTest : Node
{
    public override void _Ready()
    {
        try
        {
            var ids = new[]
            {
                "dunedin-one-way-oval-gardens", "dunedin-south-coastal-loop",
                "dunedin-rolling-loop", "dunedin-signal-hill-climbing-loop"
            };
            foreach (var id in ids) BuildRoute(id);
            GD.Print("[RouteWorldSmoke] PASS: all four route meshes constructed and released.");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError($"[RouteWorldSmoke] FAIL: {exception}");
            GetTree().Quit(1);
        }
    }

    private void BuildRoute(string id)
    {
        var timer = Stopwatch.StartNew();
        var route = RouteCatalog.GetById(id) ?? throw new InvalidOperationException($"Route package {id} is not installed.");
        var visual = new VisualRoute(route);
        var resource = $"Wodra.GameData.Routes.{id}.";
        var streets = RouteBackgroundStreetDataLoader.TryLoadEmbedded(typeof(RouteCatalog).Assembly,
            resource + "streets.json", id);
        if (streets is null || streets.Streets.Count == 0)
            throw new InvalidOperationException($"Route {id} has no background street sidecar.");
        var routeIndex = new BackgroundStreetGeometry.RouteSegmentSpatialIndex(visual.Samples);
        var world = new Node3D { Name = $"Smoke_{id}" };
        AddChild(world);
        try
        {
            world.AddChild(ProceduralRoad.BuildRibbon(visual, ProceduralRoad.WidthMeters, 0, WodraWorldStyle.Asphalt));
            var rider = new CyclistVisual { Name = "SmokeCyclist" };
            rider.Build();
            world.AddChild(rider);

            var buildingResource = resource + "buildings.json";
            var buildings = RouteBuildingDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly, buildingResource, id);
            var buildingLayer = new BuildingLayer { Name = "SmokeBuildings" };
            world.AddChild(buildingLayer);
            buildingLayer.Configure(visual, route, buildings);

            var streetLayer = new BackgroundStreetLayer { Name = "SmokeStreets" };
            world.AddChild(streetLayer);
            streetLayer.Configure(visual, streets, routeIndex);
            foreach (var distance in id == "dunedin-rolling-loop" ? new[] { 0d, 350d }
                : id == "dunedin-signal-hill-climbing-loop" ? new[] { 0d, 3500d, 8000d, 12700d }
                : new[] { 0d })
            {
                streetLayer.UpdateForRideDistance(distance);
                if (streetLayer.VisibleChunkCount == 0)
                    throw new InvalidOperationException($"Route {id} has no visible street chunks at {distance:F0} m.");
                var pose = visual.GetPoseAtDistance(distance);
                var camera = new ChaseCamera { Name = "SmokeCamera" };
                world.AddChild(camera);
                camera.Follow(ProceduralRoad.ToGodot(pose.Position), ProceduralRoad.ToGodot(pose.Forward), 0);
                world.RemoveChild(camera);
                camera.Free();
            }
            timer.Stop();
            GD.Print($"[RouteWorldSmoke] route={id} buildings={buildingLayer.BuildingsLoaded} " +
                $"buildingTriangles={buildingLayer.TriangleCount} buildingMs={buildingLayer.BuildMilliseconds:F1} " +
                $"streets={streetLayer.LoadedStreetCount} streetMeshes={streetLayer.MeshChunkCount} " +
                $"streetTriangles={streetLayer.TriangleCount} streetMs={streetLayer.BuildMilliseconds} " +
                $"worldBuildMs={timer.Elapsed.TotalMilliseconds:F1} views=road,rider,buildings,streets,camera");
        }
        finally
        {
            RemoveChild(world);
            world.Free();
        }
    }
}
