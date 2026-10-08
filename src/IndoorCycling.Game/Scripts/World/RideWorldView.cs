using System;
using System.Linq;
using Godot;

public readonly record struct RideWorldDiagnostic(
    VisualRoutePose Pose, Godot.Vector3 RiderWorldPosition,
    Godot.Vector3 RiderForward, Godot.Vector3 RiderRotation,
    Godot.Vector3 CameraWorldPosition, int NearbyLandmarkCount = 0,
    string? NearestLandmarkName = null, double? NearestLandmarkRouteDistanceMeters = null,
    Godot.Vector3? NearestLandmarkWorldPosition = null, double? NearestLandmarkDistanceMeters = null,
    int BuildingsLoaded = 0, int VisibleBuildingChunks = 0,
      int VegetationAreas = 0, int TreesLoaded = 0, int VisibleVegetationChunks = 0,
      int StreetFurnitureLoaded = 0, int NearbyStreetFurniture = 0,
      int BackgroundStreetsLoaded = 0, int VisibleStreetChunks = 0,
      int StreetFurnitureInsideRideRoad = 0, int StreetFurnitureProxiesSuppressed = 0,
      int StreetFurnitureProxiesRelocated = 0, int StreetFurnitureIntentionallyRetainedInRoad = 0,
      float RiderLateralOffsetMeters = RiderLaneMath.RiderLateralOffsetMeters,
      StreetFurnitureLayer.NearestFurnitureDebug? NearestStreetFurniture = null,
      int TerrainChunksLoaded = 0, int VisibleTerrainChunks = 0, double TerrainSpacingMeters = 0,
      int NearTerrainChunks = 0, int FarTerrainChunks = 0, int VisibleNearTerrainChunks = 0,
      int VisibleFarTerrainChunks = 0, double FarTerrainSpacingMeters = 0,
      int RoadMarkingsLoaded = 0, int NearbyCrossings = 0);

/// <summary>Read-only view of the session: no speed integration or trainer access.</summary>
public partial class RideWorldView : Node3D
{
    [Export] public bool DebugPoseLogging { get; set; }
    private RideSessionNode _session = default!;
    private RouteDefinition? _loadedRoute;
    private VisualRoute? _geometry;
    private Node3D? _routeRoot;
    private CyclistVisual _rider = default!;
    private ChaseCamera _camera = default!;
    private LandmarkLayer? _landmarkLayer;
    private BuildingLayer? _buildingLayer;
    private VegetationLayer? _vegetationLayer;
    private StreetFurnitureLayer? _streetFurnitureLayer;
    private RoadMarkingLayer? _roadMarkingLayer;
    private BackgroundStreetLayer? _backgroundStreetLayer;
    private TerrainLayer? _terrainLayer;
    private double _debugElapsed;
    private bool _wasActive;
    private VisualRoutePose _lastPose;

    public override void _Ready()
    {
        _session = GetNode<RideSessionNode>("../RideSessionNode");
        _camera = new ChaseCamera { Name = "ChaseCamera" };
        AddChild(_camera);
        _rider = new CyclistVisual { Name = "CyclistVisual" };
        _rider.Build();
        AddChild(_rider);
        AddChild(new DirectionalLight3D
        {
            Name = "Sun",
            // A fixed late-morning angle gives the route visible lateral shading
            // without introducing an animated time-of-day system.
            RotationDegrees = new Vector3(-38, -32, 0),
            LightColor = new Color(1.0f, 0.97f, 0.92f),
            LightEnergy = 0.82f,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,
            DirectionalShadowMaxDistance = 180,
            ShadowOpacity = 0.52f
        });
        var sky = new Sky
        {
            SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = new Color(0.22f, 0.43f, 0.69f),
                SkyHorizonColor = new Color(0.68f, 0.79f, 0.86f),
                GroundBottomColor = new Color(0.34f, 0.39f, 0.34f),
                GroundHorizonColor = new Color(0.67f, 0.75f, 0.76f),
                SkyCurve = 0.18f,
                GroundCurve = 0.12f,
                SunAngleMax = 12,
                SunCurve = 0.12f
            }
        };
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = sky,
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightSkyContribution = 0.72f,
                AmbientLightEnergy = 0.53f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
                TonemapExposure = 0.92f
            }
        });
        Hide();
    }

    public override void _Process(double delta)
    {
        if (!_session.IsRideActive || _session.CurrentRoute is null)
        {
            Hide();
            _camera.Current = false;
            _wasActive = false;
            return;
        }
        if (!_wasActive || !ReferenceEquals(_loadedRoute, _session.CurrentRoute))
        {
            LoadRoute(_session.CurrentRoute);
            _camera.ResetFollow();
        }
        _wasActive = true;
        Show();
        _camera.Current = true;
        var distance = _session.CurrentRideState.DistanceMeters;
        var pose = _geometry!.GetPoseAtDistance(distance);
        _lastPose = pose;
        var routePosition = ProceduralRoad.ToGodot(pose.Position) + Vector3.Up * 0.04f;
        var forward = ProceduralRoad.ToGodot(pose.Forward);
        var riderPositionN = RiderLaneMath.OffsetPosition(
            new System.Numerics.Vector3(routePosition.X, routePosition.Y, routePosition.Z),
            new System.Numerics.Vector3(forward.X, forward.Y, forward.Z));
        var position = new Vector3(riderPositionN.X, riderPositionN.Y, riderPositionN.Z);
        var rideState = _session.CurrentRideState;
        _rider.UpdateFromRideState(position, forward, rideState.VirtualSpeedKph,
            rideState.CurrentCadenceRpm, _loadedRoute!.GetGradientAt(distance),
            _session.IsRidePaused, delta);
        _camera.Follow(position, forward, delta);
        _landmarkLayer?.UpdateForRideDistance(distance);
        _buildingLayer?.UpdateForRideDistance(distance);
        _vegetationLayer?.UpdateForRideDistance(distance);
        _streetFurnitureLayer?.UpdateForRideDistance(distance);
        _roadMarkingLayer?.UpdateForRideDistance(distance);
        _backgroundStreetLayer?.UpdateForRideDistance(distance);
        _terrainLayer?.UpdateForRideDistance(distance);
        _debugElapsed += delta;
        if (DebugPoseLogging && _debugElapsed >= 1)
        {
            _debugElapsed = 0;
            GD.Print($"Ride view: total={distance:F1}m loop={pose.LoopDistanceMeters:F1}/{_geometry.LengthMeters:F1}m " +
                $"samples={pose.SampleIndexBefore}->{pose.SampleIndexAfter}@{pose.Interpolation:F3} " +
                $"position={position} segment={_loadedRoute!.GetSampleAt(distance).SegmentLabel} " +
                $"elevation={_loadedRoute.GetSampleAt(distance).ElevationMeters:F1}m " +
                $"raw={_loadedRoute.GetSampleAt(distance).RawElevationMeters:F1}m " +
                $"visualY={pose.Position.Y:F2}m grade={_loadedRoute.GetGradientAt(distance):F2}% " +
                $"riderLaneOffset={RiderLaneMath.RiderLateralOffsetMeters:F2}m-left");
            if (_terrainLayer is { } terrainLayer)
                GD.Print($"Terrain chunks loaded: {terrainLayer.TerrainChunksLoaded}; " +
                    $"visible terrain chunks: {terrainLayer.VisibleTerrainChunks}; " +
                    $"terrain spacing: {terrainLayer.TerrainSpacingMeters:F0} m");
            if(_streetFurnitureLayer is { } furnitureLayer)
            {
                GD.Print($"Street furniture intrusion: sourceInside={furnitureLayer.SourceItemsInsideRideRoad} " +
                    $"suppressed={furnitureLayer.PhysicalProxiesSuppressed} relocated={furnitureLayer.ProxiesRelocatedRoadside} " +
                    $"retainedInRoad={furnitureLayer.IntentionallyRetainedInRoad}");
                if(furnitureLayer.GetNearestDebugState(distance) is { } nearestFurniture)
                    GD.Print($"Nearest street furniture: OSM {nearestFurniture.OsmId} {nearestFurniture.Category} " +
                        $"source={nearestFurniture.SourcePosition} visual={nearestFurniture.VisualPosition} " +
                        $"relocated={(nearestFurniture.Relocated ? "yes" : "no")} suppressed={(nearestFurniture.ProxySuppressed ? "yes" : "no")}");
            }
            if (_roadMarkingLayer is { } markingLayer)
                GD.Print($"Road markings loaded: {markingLayer.LoadedMarkingCount}; nearby crossings: {markingLayer.NearbyCrossingCount}");
        }
    }

    public RideWorldDiagnostic GetDiagnosticState()
    {
        var forward = ProceduralRoad.ToGodot(_lastPose.Forward);
        var distance = _session.CurrentRideState.DistanceMeters;
        var landmarks = _landmarkLayer?.GetDebugState(distance, _rider.GlobalPosition) ?? default;
        return new RideWorldDiagnostic(_lastPose, _rider.GlobalPosition, forward,
            _rider.GlobalRotation, _camera.GlobalPosition, landmarks.NearbyCount, landmarks.Name,
            landmarks.RouteDistanceMeters, landmarks.WorldPosition, landmarks.RiderDistanceMeters,
            _buildingLayer?.BuildingsLoaded ?? 0, _buildingLayer?.VisibleChunkCount ?? 0,
            _vegetationLayer?.VegetationAreaCount ?? 0, _vegetationLayer?.TreesLoaded ?? 0,
            _vegetationLayer?.VisibleVegetationChunks ?? 0,
            _streetFurnitureLayer?.LoadedCount ?? 0, _streetFurnitureLayer?.NearbyCount ?? 0,
            _backgroundStreetLayer?.LoadedStreetCount ?? 0, _backgroundStreetLayer?.VisibleChunkCount ?? 0,
            _streetFurnitureLayer?.SourceItemsInsideRideRoad ?? 0,
            _streetFurnitureLayer?.PhysicalProxiesSuppressed ?? 0,
            _streetFurnitureLayer?.ProxiesRelocatedRoadside ?? 0,
            _streetFurnitureLayer?.IntentionallyRetainedInRoad ?? 0,
            RiderLaneMath.RiderLateralOffsetMeters,
            _streetFurnitureLayer?.GetNearestDebugState(distance),
            _terrainLayer?.TerrainChunksLoaded ?? 0,
            _terrainLayer?.VisibleTerrainChunks ?? 0,
            _terrainLayer?.TerrainSpacingMeters ?? 0,
            _terrainLayer?.NearTerrainChunks ?? 0, _terrainLayer?.FarTerrainChunks ?? 0,
            _terrainLayer?.VisibleNearTerrainChunks ?? 0, _terrainLayer?.VisibleFarTerrainChunks ?? 0,
            _terrainLayer?.FarTerrainSpacingMeters ?? 0,
            _roadMarkingLayer?.LoadedMarkingCount ?? 0,
            _roadMarkingLayer?.NearbyCrossingCount ?? 0);
    }

    private void LoadRoute(RouteDefinition route)
    {
        if (_routeRoot is not null)
        {
            RemoveChild(_routeRoot);
            _routeRoot.QueueFree();
        }
        _loadedRoute = route;
        _geometry = new VisualRoute(route);
        var groundPlaneY = _geometry.GroundPlaneY;
        _routeRoot = new Node3D { Name = "RouteGeometry" };
        AddChild(_routeRoot);
        _routeRoot.AddChild(ProceduralRoad.BuildRibbon(_geometry, 60, -0.08f, new Color(0.28f, 0.45f, 0.22f)));
        var asphalt = new Color(0.17f, 0.18f, 0.2f);
        _routeRoot.AddChild(ProceduralRoad.BuildRibbon(_geometry, ProceduralRoad.WidthMeters, 0, asphalt));
        var junctionFill = ProceduralRoad.BuildSharpJoinFills(_geometry, ProceduralRoad.WidthMeters, asphalt);
        if (junctionFill is not null) _routeRoot.AddChild(junctionFill);
        _routeRoot.AddChild(ProceduralRoad.BuildRibbon(_geometry, 0.12f, 0.015f, new Color(0.9f, 0.82f, 0.35f)));
        foreach (float offset in new[] { -3.25f, 3.25f })
            _routeRoot.AddChild(ProceduralRoad.BuildRibbon(_geometry, 0.12f, 0.02f, Colors.White, offset));
        _routeRoot.AddChild(ProceduralRoad.BuildKerbsAndFootpaths(_geometry));
        _routeRoot.AddChild(RoadsideDelineators.Build(_geometry));
        _landmarkLayer = null;
        _buildingLayer = null;
        _vegetationLayer = null;
        _streetFurnitureLayer = null;
        _roadMarkingLayer = null;
        _backgroundStreetLayer = null;
        _terrainLayer = null;
        RouteVegetationData? vegetationData = null;
        if (route.Id == "dunedin-one-way-oval-gardens")
        {
            var terrain = new TerrainLayer { Name = "LINZTerrain" };
            if (terrain.Configure(_geometry, "GWift.GameData.Routes.dunedin-one-way-oval-gardens.terrain.json", route.Id))
            {
                _terrainLayer = terrain;
                _routeRoot.AddChild(terrain);
                _terrainLayer.UpdateForRideDistance(_session.CurrentRideState.DistanceMeters);
            }
            var buildings = RouteBuildingDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
                "GWift.GameData.Routes.dunedin-one-way-oval-gardens.buildings.json", route.Id);
            var originElevation = route.Points?.FirstOrDefault()?.ElevationMeters ?? 0;
            if (buildings.Count > 0)
                groundPlaneY = Math.Min(groundPlaneY,
                    (float)(buildings.Min(item => item.BaseElevationMeters!.Value - originElevation) - VisualRoute.GroundClearanceMeters));
            _buildingLayer = new BuildingLayer { Name = "RouteBuildings" };
            _routeRoot.AddChild(_buildingLayer);
            _buildingLayer.Configure(_geometry, route, buildings);
            _buildingLayer.UpdateForRideDistance(_session.CurrentRideState.DistanceMeters);
            var landmarks = RouteLandmarkDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
                "GWift.GameData.Routes.dunedin-one-way-oval-gardens.landmarks.json", route.Id);
            _landmarkLayer = new LandmarkLayer { Name = "RouteLandmarks" };
            _routeRoot.AddChild(_landmarkLayer);
            _landmarkLayer.Configure(_geometry, route, landmarks, buildings);
            _landmarkLayer.UpdateForRideDistance(_session.CurrentRideState.DistanceMeters);
            vegetationData = RouteVegetationDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
                "GWift.GameData.Routes.dunedin-one-way-oval-gardens.vegetation.json", route.Id);
            var vegOriginElevation = route.Points?.FirstOrDefault()?.ElevationMeters ?? 0;
            var renderableAreas = vegetationData.Areas.Where(item => item.RenderGround && item.BaseElevationMeters is not null).ToArray();
            if (renderableAreas.Length > 0)
                groundPlaneY = Math.Min(groundPlaneY,
                    (float)(renderableAreas.Min(item => item.BaseElevationMeters!.Value - vegOriginElevation) - VisualRoute.GroundClearanceMeters));
            _vegetationLayer = new VegetationLayer { Name = "RouteVegetation" };
            _routeRoot.AddChild(_vegetationLayer);
            _vegetationLayer.Configure(_geometry, route, vegetationData);
            _vegetationLayer.UpdateForRideDistance(_session.CurrentRideState.DistanceMeters);
            var furniture = RouteStreetFurnitureDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
                "GWift.GameData.Routes.dunedin-one-way-oval-gardens.street-furniture.json", route.Id);
            _streetFurnitureLayer = new StreetFurnitureLayer { Name = "RouteStreetFurniture" };
            _routeRoot.AddChild(_streetFurnitureLayer);
            _streetFurnitureLayer.Configure(_geometry, route, furniture);
            _streetFurnitureLayer.UpdateForRideDistance(_session.CurrentRideState.DistanceMeters);
            _roadMarkingLayer = new RoadMarkingLayer { Name = "RoadMarkings" };
            _routeRoot.AddChild(_roadMarkingLayer);
            _roadMarkingLayer.Configure(_geometry, furniture);
            _roadMarkingLayer.UpdateForRideDistance(_session.CurrentRideState.DistanceMeters);
            var streets=RouteBackgroundStreetDataLoader.TryLoadEmbedded(typeof(RouteCatalog).Assembly,
                "GWift.GameData.Routes.dunedin-one-way-oval-gardens.streets.json",route.Id);
            if(streets is not null){
                _backgroundStreetLayer=new BackgroundStreetLayer{Name="BackgroundStreets"};
                _routeRoot.AddChild(_backgroundStreetLayer);
                _backgroundStreetLayer.Configure(_geometry,streets);
                _backgroundStreetLayer.UpdateForRideDistance(_session.CurrentRideState.DistanceMeters);
            }
        }
        var positions = _geometry.Samples.Select(v => v.Position).ToArray();
        float minX = positions.Min(p => p.X), maxX = positions.Max(p => p.X);
        float minZ = positions.Min(p => p.Z), maxZ = positions.Max(p => p.Z);
        if (_terrainLayer is null)
            _routeRoot.AddChild(new MeshInstance3D
            {
                Name = "Ground",
                Position = new Vector3((minX + maxX) / 2, groundPlaneY, (minZ + maxZ) / 2),
                Mesh = new PlaneMesh { Size = new Vector2(maxX - minX + 400, maxZ - minZ + 400) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.36f, 0.18f), Roughness = 1 }
            });
        GameLogger.Log("visual_route_loaded", new
        {
            route.Id, route.DistanceMeters, samples = _geometry.Samples.Count,
            closureCorrectionMeters = _geometry.ClosureCorrection.ToString(),
            landmarkCount = _landmarkLayer?.CreatedLandmarkCount ?? 0,
            landmarkSceneNodes = _landmarkLayer?.CreatedNodeCount ?? 0,
            buildingsLoaded = _buildingLayer?.BuildingsLoaded ?? 0,
            buildingTriangles = _buildingLayer?.TriangleCount ?? 0,
            buildingFootprintVertices = _buildingLayer?.FootprintVertexCount ?? 0,
            buildingChunks = _buildingLayer?.ChunkCount ?? 0,
            buildingMeshInstances = _buildingLayer?.MeshInstanceCount ?? 0,
            buildingMeshBuildMilliseconds = _buildingLayer?.BuildMilliseconds ?? 0,
            vegetationAreas = _vegetationLayer?.VegetationAreaCount ?? 0,
            vegetationTrees = _vegetationLayer?.TreesLoaded ?? 0,
            vegetationTreeMultiMeshes = _vegetationLayer?.MultiMeshCount ?? 0,
            vegetationAreaMeshInstances = _vegetationLayer?.AreaMeshInstanceCount ?? 0,
            vegetationAreaTriangles = _vegetationLayer?.AreaTriangleCount ?? 0,
            vegetationSceneNodes = _vegetationLayer?.CreatedSceneNodeCount ?? 0,
             vegetationBuildMilliseconds = _vegetationLayer?.BuildMilliseconds ?? 0,
             streetFurnitureLoaded = _streetFurnitureLayer?.LoadedCount ?? 0,
             streetFurnitureInsideRideRoad = _streetFurnitureLayer?.SourceItemsInsideRideRoad ?? 0,
             streetFurnitureProxiesSuppressed = _streetFurnitureLayer?.PhysicalProxiesSuppressed ?? 0,
             streetFurnitureProxiesRelocatedRoadside = _streetFurnitureLayer?.ProxiesRelocatedRoadside ?? 0,
             streetFurnitureIntentionallyRetainedInRoad = _streetFurnitureLayer?.IntentionallyRetainedInRoad ?? 0,
             streetFurnitureMultiMeshes = _streetFurnitureLayer?.MultiMeshCount ?? 0,
             streetFurnitureSceneNodes = _streetFurnitureLayer?.SceneNodeCount ?? 0,
             streetFurnitureBuildMilliseconds = _streetFurnitureLayer?.BuildMilliseconds ?? 0,
             roadMarkingsLoaded = _roadMarkingLayer?.LoadedMarkingCount ?? 0,
             roadMarkingTriangles = _roadMarkingLayer?.MarkingTriangleCount ?? 0,
             roadMarkingMeshInstances = _roadMarkingLayer?.MeshInstanceCount ?? 0,
             nearbyCrossings = _roadMarkingLayer?.NearbyCrossingCount ?? 0,
             backgroundStreetsLoaded = _backgroundStreetLayer?.LoadedStreetCount ?? 0,
             visibleStreetChunks = _backgroundStreetLayer?.VisibleChunkCount ?? 0,
             backgroundStreetMeshes = _backgroundStreetLayer?.MeshChunkCount ?? 0,
             backgroundStreetTriangles = _backgroundStreetLayer?.TriangleCount ?? 0,
             backgroundStreetBuildMilliseconds = _backgroundStreetLayer?.BuildMilliseconds ?? 0,
             terrainChunksLoaded = _terrainLayer?.TerrainChunksLoaded ?? 0,
             visibleTerrainChunks = _terrainLayer?.VisibleTerrainChunks ?? 0,
             terrainSpacingMeters = _terrainLayer?.TerrainSpacingMeters ?? 0,
             nearTerrainChunks = _terrainLayer?.NearTerrainChunks ?? 0,
             farTerrainChunks = _terrainLayer?.FarTerrainChunks ?? 0,
             visibleNearTerrainChunks = _terrainLayer?.VisibleNearTerrainChunks ?? 0,
             visibleFarTerrainChunks = _terrainLayer?.VisibleFarTerrainChunks ?? 0,
             farTerrainSpacingMeters = _terrainLayer?.FarTerrainSpacingMeters ?? 0,
             terrainTriangles = _terrainLayer?.TerrainTriangleCount ?? 0,
             terrainRoadClearanceAdjustedVertices = _terrainLayer?.RoadClearanceAdjustedVertices ?? 0,
             terrainBuildMilliseconds = _terrainLayer?.BuildMilliseconds ?? 0,
            cyclistNodes = _rider.ApproximateNodeCount,
            cyclistApproximateTriangles = _rider.ApproximateTriangleCount
        });
    }
}
