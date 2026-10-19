using System;
using System.Diagnostics;
using Godot;

/// <summary>Headless Godot smoke coverage for the real built-in and installed Dunedin world packages.</summary>
public partial class RouteWorldSmokeTest : Node
{
    public override async void _Ready()
    {
        try
        {
            var ids = new[]
            {
                "dunedin-one-way-oval-gardens", "dunedin-south-coastal-loop",
                "dunedin-rolling-loop", "dunedin-signal-hill-climbing-loop"
            };
            foreach (var id in ids) await BuildRoute(id);
            GD.Print("[RouteWorldSmoke] PASS: all four route meshes constructed and released.");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError($"[RouteWorldSmoke] FAIL: {exception}");
            GetTree().Quit(1);
        }
    }

    private async System.Threading.Tasks.Task BuildRoute(string id)
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
            if (buildingLayer.BuildingsLoaded + buildingLayer.BuildingsSuppressedForRideCorridor != buildings.Count)
                throw new InvalidOperationException($"Route {id} neither built nor corridor-filtered every source building.");

            var streetLayer = new BackgroundStreetLayer { Name = "SmokeStreets" };
            world.AddChild(streetLayer);
            streetLayer.Configure(visual, streets, routeIndex);
            var roadCorridors = BackgroundStreetGeometry.BuildTerrainCorridors(visual, streets, routeIndex);
            var terrainLayer = new TerrainLayer { Name = "SmokeTerrain" };
            world.AddChild(terrainLayer);
            if (!terrainLayer.Configure(visual, resource + "terrain.json", id, roadCorridors))
                throw new InvalidOperationException($"Route {id} has no terrain mesh.");
            var vegetationData = RouteVegetationDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
                resource + "vegetation.json", id);
            var vegetationLayer = new VegetationLayer { Name = "SmokeVegetation" };
            world.AddChild(vegetationLayer);
            vegetationLayer.Configure(visual, route, vegetationData, roadCorridors);
            var camera = new ChaseCamera { Name = "SmokeCamera" };
            world.AddChild(camera);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (id == "dunedin-rolling-loop")
                await VerifyCameraVolumeCollision(visual, camera, world);
            var distances = id == "dunedin-rolling-loop"
                ? new[] { 0d, 50d, 60d, 85d, 200d, 210d, 225.564230d, 242.390684d, 350d,
                    450d, 480.106d, 550.746d, 573.300d, 600d, 225.564230d }
                : id == "dunedin-signal-hill-climbing-loop" ? new[] { 0d, 3500d, 8000d, 12700d }
                : id == "dunedin-south-coastal-loop" ? new[] { 0d, 5000d }
                : new[] { 0d, 5000d };
            var previousDistance = 0d;
            foreach (var distance in distances)
            {
                streetLayer.UpdateForRideDistance(distance);
                terrainLayer.UpdateForRideDistance(distance);
                vegetationLayer.UpdateForRideDistance(distance);
                buildingLayer.UpdateForRideDistance(distance);
                if (streetLayer.VisibleChunkCount == 0)
                    throw new InvalidOperationException($"Route {id} has no visible street chunks at {distance:F0} m.");
                if (distance + 100 < previousDistance) camera.ResetFollow();
                var pose = visual.GetPoseAtDistance(distance);
                camera.Follow(ProceduralRoad.ToGodot(pose.Position), ProceduralRoad.ToGodot(pose.Forward), 1d / 60);
                if (!Finite(camera.GlobalPosition) || !Finite(camera.DesiredPosition))
                    throw new InvalidOperationException($"Route {id} camera produced a nonfinite position at {distance:F3} m.");
                if (id == "dunedin-rolling-loop" && distance is >= 210 and <= 600)
                    GD.Print($"[CameraClearanceSmoke] route={id} distance={distance:F3}m view={camera.View} " +
                        $"result={camera.ClearanceResult} source={camera.ObstructionSource ?? "none"} " +
                        $"desired={camera.DesiredPosition} resolved={camera.GlobalPosition}");
                previousDistance = distance;
            }

            if (id == "dunedin-rolling-loop")
            {
                foreach (var view in RiderCameraViewCycle.Views)
                foreach (var lane in new[] { -1.75f, 0f, 1.75f })
                {
                    var pose = visual.GetPoseAtDistance(225.564230);
                    var position = RiderLaneMath.OffsetPosition(pose.Position, pose.Forward, lane);
                    camera.SetView(view);
                    camera.ResetFollow();
                    buildingLayer.UpdateForRideDistance(225.564230);
                    terrainLayer.UpdateForRideDistance(225.564230);
                    camera.Follow(ProceduralRoad.ToGodot(position), ProceduralRoad.ToGodot(pose.Forward), 0);
                    if (!Finite(camera.GlobalPosition))
                        throw new InvalidOperationException($"Camera clearance failed at Rolling City 225.564 m, lane {lane}, view {view}.");
                }

                foreach (var speedMetersPerSecond in new[] { 30d / 3.6, 62d / 3.6 })
                {
                    camera.SetView(RiderCameraView.GameplayRefined);
                    camera.ResetFollow();
                    var positionDistance = 210d;
                    for (var frame = 0; frame < 180; frame++)
                    {
                        positionDistance = Math.Min(250, positionDistance + speedMetersPerSecond / 60);
                        var pose = visual.GetPoseAtDistance(positionDistance);
                        buildingLayer.UpdateForRideDistance(positionDistance);
                        terrainLayer.UpdateForRideDistance(positionDistance);
                        camera.Follow(ProceduralRoad.ToGodot(pose.Position), ProceduralRoad.ToGodot(pose.Forward), 1d / 60);
                        if (!Finite(camera.GlobalPosition))
                            throw new InvalidOperationException($"Camera sweep failed at {speedMetersPerSecond * 3.6:F0} km/h and {positionDistance:F3} m.");
                    }
                }
            }
            timer.Stop();
            GD.Print($"[RouteWorldSmoke] route={id} buildings={buildingLayer.BuildingsLoaded} " +
                $"corridorSuppressed={buildingLayer.BuildingsSuppressedForRideCorridor} " +
                $"buildingTriangles={buildingLayer.TriangleCount} buildingMs={buildingLayer.BuildMilliseconds:F1} " +
                $"streets={streetLayer.LoadedStreetCount} streetMeshes={streetLayer.MeshChunkCount} " +
                $"streetTriangles={streetLayer.TriangleCount} streetMs={streetLayer.BuildMilliseconds} " +
                $"terrainChunks={terrainLayer.TerrainChunksLoaded} terrainTriangles={terrainLayer.TerrainTriangleCount} " +
                $"terrainCutoutCells={terrainLayer.RoadCutoutCellCount} vegetationAreas={vegetationLayer.VegetationAreaCount} " +
                $"trees={vegetationLayer.TreesLoaded} suppressedRoadOverlays={vegetationLayer.RoadOverlapSuppressedAreaCount} " +
                $"vegetationMeshes={vegetationLayer.AreaMeshInstanceCount} vegetationMs={vegetationLayer.BuildMilliseconds:F1} " +
                $"worldBuildMs={timer.Elapsed.TotalMilliseconds:F1} views=road,rider,buildings,streets,terrain,vegetation,camera");
        }
        finally
        {
            RemoveChild(world);
            world.Free();
        }
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X)
        && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private async System.Threading.Tasks.Task VerifyCameraVolumeCollision(VisualRoute route,
        ChaseCamera camera, Node3D world)
    {
        var distance = 225.564230;
        var pose = route.GetPoseAtDistance(distance);
        var rider = ProceduralRoad.ToGodot(pose.Position);
        var forward = ProceduralRoad.ToGodot(pose.Forward);
        var desired = ChaseCameraMath.FollowPosition(
            new System.Numerics.Vector3(rider.X, rider.Y, rider.Z),
            new System.Numerics.Vector3(forward.X, forward.Y, forward.Z), camera.View);
        var target = ProceduralRoad.ToGodot(new System.Numerics.Vector3(desired.X, desired.Y, desired.Z));
        var anchor = rider + Vector3.Up * 1.05f;
        var wall = new StaticBody3D
        {
            Name = "SmokeCameraWall",
            GlobalPosition = anchor.Lerp(target, .7f),
            CollisionLayer = 1u << 1,
            CollisionMask = 0
        };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.4f, 3f, 1.4f) } });
        world.AddChild(wall);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        camera.ResetFollow();
        camera.Follow(rider, forward, 0);
        if (camera.ClearanceResult != "boom-clamped" || camera.ObstructionSource != wall.Name.ToString())
            throw new InvalidOperationException($"Camera volume sweep did not detect the synthetic wall: {camera.ClearanceResult}/{camera.ObstructionSource}.");

        wall.GlobalPosition = camera.GlobalPosition;
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        var nextPose = route.GetPoseAtDistance(242.390684);
        camera.Follow(ProceduralRoad.ToGodot(nextPose.Position), ProceduralRoad.ToGodot(nextPose.Forward), 1d / 60);
        if (camera.ClearanceResult != "camera-overlap-retracted")
            throw new InvalidOperationException($"A camera beginning inside a collision mesh was not retracted: {camera.ClearanceResult}.");
        world.RemoveChild(wall);
        wall.Free();
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }
}
