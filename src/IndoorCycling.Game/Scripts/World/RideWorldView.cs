using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;

public sealed record AiRiderDebugState(string Name, double RelativeDistanceMeters,
    double RouteDistanceMeters, double SpeedKph, int PowerWatts, int CadenceRpm);

public sealed record RemoteHumanRiderDebugState(string RiderId, string Name, bool HasState,
	double? RelativeDistanceMeters, double? StateAgeSeconds, bool VisualExists, bool VisualVisible,
	bool WithinVisualRange, bool? BehindCamera, bool? InsideCameraFrustum);

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
      int RoadMarkingsLoaded = 0, int NearbyCrossings = 0,
      int AmbientCars = 0, int AmbientPedestrians = 0,
      int EligibleCarPaths = 0, int EligiblePedestrianPaths = 0,
      long CarSpawnAttempts = 0, long PedestrianSpawnAttempts = 0,
      long CarOutsideSpawnRadius = 0, long PedestrianOutsideSpawnRadius = 0,
      long CarTooCloseToRider = 0, long PedestrianTooCloseToRider = 0,
      long CarNoEligiblePath = 0, long PedestrianNoValidPath = 0,
      long CarInvalidTransform = 0, long PedestrianInvalidTransform = 0,
      long CarIllegalOnewayDirections = 0,
      int TotalStreetPolylines = 0, int EligibleStreetPolylines = 0,
      int InvalidOrShortCarPaths = 0, int InvalidOrShortPedestrianPaths = 0,
      int PedestrianSeamOrEndSamplesExcluded = 0,
      Godot.Vector3? FirstCarPosition = null, double? FirstCarDistance = null,
      string? FirstCarRoadName = null, string? FirstCarRoadClass = null,
      double? FirstCarSpeedMps = null, bool? FirstCarVisible = null,
      Godot.Vector3? FirstPedestrianPosition = null, double? FirstPedestrianDistance = null,
      double? FirstPedestrianSpeedMps = null, bool? FirstPedestrianVisible = null,
      int AiRiders = 0, string? NearestAiName = null,
      double? NearestAiRelativeDistanceMeters = null, double? NearestAiSpeedKph = null,
      IReadOnlyList<AiRiderDebugState>? NearestAiRiders = null,
      IReadOnlyList<RemoteHumanRiderDebugState>? RemoteHumanRiders = null);

/// <summary>Read-only view of the session: no speed integration or trainer access.</summary>
public partial class RideWorldView : Node3D
{
    [Export] public bool DebugPoseLogging { get; set; }
    [Export] public bool UseOriginalGameplayCamera { get; set; }
    [Export] public int AiRiderTargetCount { get; set; } = AiRiderPopulation.DefaultTargetCount;
    [Export] public int AiRiderMaximumCount { get; set; } = AiRiderPopulation.DefaultMaximumCount;
    [Export] public int AiRiderRandomSeed { get; set; } = AiRiderPopulation.DefaultSeed;
    private RideSessionNode _session = default!;
    private RouteDefinition? _loadedRoute;
    private VisualRoute? _geometry;
    private Node3D? _routeRoot;
    private CyclistVisual _rider = default!;
    private Node3D? _aiVisualRoot;
    private Node3D? _remoteVisualRoot;
    private readonly Dictionary<string, CyclistVisual> _remoteVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Label3D> _remoteNameLabels = new(StringComparer.Ordinal);
    private const double RemoteVisualRadiusMeters = 300;
    private LocalRiderStateSource? _riderStateSource;
    private AiRiderPopulation? _aiPopulation;
    private readonly Dictionary<string, CyclistVisual> _riderVisuals = new(StringComparer.Ordinal);
    private ChaseCamera _camera = default!;
    private RideMenuPanelController? _rideMenuPanel;
    private HudController? _hudController;
    private DeviceConnectionPanelController? _deviceConnectionPanel;
    private readonly SteeringLateralController _steering = new();
    private bool _steerLeftHeld;
    private bool _steerRightHeld;
    private DateTimeOffset _lastSteeringSampleAt = DateTimeOffset.MinValue;
    private DateTimeOffset _keyboardControllerSampleBaseline = DateTimeOffset.MinValue;
    private LandmarkLayer? _landmarkLayer;
    private BuildingLayer? _buildingLayer;
    private VegetationLayer? _vegetationLayer;
    private StreetFurnitureLayer? _streetFurnitureLayer;
    private RoadMarkingLayer? _roadMarkingLayer;
    private BackgroundStreetLayer? _backgroundStreetLayer;
    private AmbientActorManager? _ambientActorManager;
    private TerrainLayer? _terrainLayer;
    private CollectibleCatLayer? _collectibleLayer;
    private bool _gameFeatureToggledDuringAttempt;
    private double _lastChallengeDistance;
    private double _lastChallengeElapsed;
    private double _steeringDiagnosticElapsed;
    public string? LastChallengeSummary { get; private set; }
    private double _debugElapsed;
    private bool _wasActive;
    private VisualRoutePose _lastPose;
    private readonly RouteWorldLoadAttempt<RouteDefinition> _routeLoadAttempt = new();
#if DEBUG
    private IReadOnlyList<RouteBuildingData> _debugBuildings = [];
    private RouteVegetationData? _debugVegetation;
    private int _debugSceneryIsolation;
#endif

    public string CameraInspectionLabel => _camera is null ? "Gameplay (refined)" : FormatCameraView(_camera.View);
    public double CurrentLateralOffsetMeters => _steering.LateralOffsetMeters;

    public override void _Ready()
    {
        _session = GetNode<RideSessionNode>("../RideSessionNode");
        _rideMenuPanel = GetNodeOrNull<RideMenuPanelController>("../RideMenuPanel");
        _hudController = GetNodeOrNull<HudController>("../HudController");
        _deviceConnectionPanel = GetNodeOrNull<DeviceConnectionPanelController>("../DeviceConnectionPanel");
        SetProcessInput(true);
        _camera = new ChaseCamera { Name = "ChaseCamera" };
        if (UseOriginalGameplayCamera) _camera.SetView(RiderCameraView.GameplayOriginal);
        AddChild(_camera);
        _rider = new CyclistVisual { Name = "CyclistVisual" };
        _rider.Build();
        AddChild(_rider);
        _riderVisuals["player"] = _rider;
        AddChild(new DirectionalLight3D
        {
            Name = "Sun",
            // Fixed golden-hour light keeps route geometry legible and repeatable.
            RotationDegrees = new Vector3(-24, -34, 0),
            LightColor = WodraWorldStyle.Sunlight,
            LightEnergy = WodraWorldStyle.SunEnergy,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,
            DirectionalShadowMaxDistance = WodraWorldStyle.ShadowDistanceMeters,
            ShadowOpacity = 0.42f
        });
        var sky = new Sky
        {
            SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = WodraWorldStyle.SkyTop,
                SkyHorizonColor = WodraWorldStyle.SkyHorizon,
                GroundBottomColor = WodraWorldStyle.GroundHorizon.Darkened(0.22f),
                GroundHorizonColor = WodraWorldStyle.GroundHorizon,
                SkyCurve = 0.24f,
                GroundCurve = 0.18f,
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
                AmbientLightSkyContribution = 0.58f,
                AmbientLightEnergy = WodraWorldStyle.AmbientEnergy,
                ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
                FogEnabled = true,
                FogLightColor = WodraWorldStyle.GroundHorizon,
                FogLightEnergy = 0.72f,
                FogSunScatter = 0.16f,
                FogDensity = WodraWorldStyle.FogDensity,
                FogAerialPerspective = WodraWorldStyle.FogAerialPerspective,
                FogSkyAffect = WodraWorldStyle.FogSkyAffect,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
                TonemapExposure = 0.92f
            }
        });
        Hide();
#if DEBUG
        var assembly = typeof(RideWorldView).Assembly;
        var debugAssemblyPath = ProjectSettings.GlobalizePath("res://.godot/mono/temp/bin/Debug/IndoorCycling.Game.dll");
        var debugAssemblyTime = System.IO.File.Exists(debugAssemblyPath)
            ? System.IO.File.GetLastWriteTimeUtc(debugAssemblyPath).ToString("O") : "missing";
        GD.Print($"[CameraInspect] RideWorldView ready node={GetPath()} visible={IsVisibleInTree()} " +
            $"inputProcessing={IsProcessingInput()} debugBuild=true assembly={assembly.GetName().Version} " +
            $"mvid={assembly.ManifestModule.ModuleVersionId} loadedPath='{assembly.Location}' " +
            $"debugDll={debugAssemblyPath} debugDllModifiedUtc={debugAssemblyTime} " +
            $"camera={_camera.GetPath()} initial={_camera.View} current={_camera.GlobalPosition}");
#endif
    }

    public RiderCameraView CurrentCameraView => _camera.View;
    public VisualRoute? RouteGeometry => _geometry;

    public string CycleCameraInspectionView()
    {
        var before = _camera.View;
        var positionBefore = _camera.GlobalPosition;
        var view = _camera.CycleView();
        SnapCameraToSelectedView();
        var label = FormatCameraView(view);
        GameLogger.Log("camera_inspection_view", new { view = label });
#if DEBUG
        GD.Print($"[CameraInspect] cycle accepted source=F6 before={before} after={view} " +
            $"positionBefore={positionBefore} positionAfter={_camera.GlobalPosition} " +
            $"transform={_camera.GlobalTransform} current={_camera.Current}");
#endif
        return label;
    }

    public void SelectCameraInspectionView(RiderCameraView view)
    {
        if (_session?.IsRideActive != true) return;
        var before = _camera.View;
        var positionBefore = _camera.GlobalPosition;
        _camera.SetView(view);
        SnapCameraToSelectedView();
        var label = FormatCameraView(_camera.View);
        _hudController?.ShowCameraInspectionView(label);
#if DEBUG
        GD.Print($"[CameraInspect] selector accepted before={before} after={_camera.View} " +
            $"positionBefore={positionBefore} positionAfter={_camera.GlobalPosition} " +
            $"transform={_camera.GlobalTransform} current={_camera.Current}");
#endif
    }

    private void SnapCameraToSelectedView()
    {
        if (_session?.IsRideActive != true) return;
        _camera.Current = true;
        _camera.Follow(_rider.GlobalPosition, ProceduralRoad.ToGodot(_lastPose.Forward), 0);
    }

    // This shared gameplay input path runs before Control focus/unhandled-input
    // routing and is available in both Debug and Release builds.
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey key) return;
        var keyCode = key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode;
        if (keyCode is Key.Left or Key.Right)
        {
            if (!key.Pressed)
            {
                if (keyCode == Key.Left) _steerLeftHeld = false;
                else _steerRightHeld = false;
                _keyboardControllerSampleBaseline = _deviceConnectionPanel?.LatestSteeringInputAt ?? _lastSteeringSampleAt;
            }
            else if (!key.Echo && _session?.IsRideActive == true && _rideMenuPanel?.IsMenuVisible != true && !IsTypingIntoControl())
            {
                if (keyCode == Key.Left) _steerLeftHeld = true;
                else _steerRightHeld = true;
                _keyboardControllerSampleBaseline = _deviceConnectionPanel?.LatestSteeringInputAt ?? _lastSteeringSampleAt;
                GetViewport().SetInputAsHandled();
            }
            return;
        }
#if DEBUG
        if ((key.Keycode == Key.F7 || key.PhysicalKeycode == Key.F7) && key.Pressed && !key.Echo
            && _session?.IsRideActive == true && _rideMenuPanel?.IsMenuVisible != true)
        {
            CycleDebugSceneryIsolation();
            GetViewport().SetInputAsHandled();
            return;
        }
#endif
        if (key.Keycode != Key.F6 && key.PhysicalKeycode != Key.F6) return;

        var rideActive = _session?.IsRideActive == true;
        var menuVisible = _rideMenuPanel?.IsMenuVisible == true;
        var accepted = CameraInspectionInputPolicy.CanCycle(rideActive, menuVisible, key.Pressed, key.Echo);
#if DEBUG
        GD.Print($"[CameraInspect] F6 received pressed={key.Pressed} echo={key.Echo} " +
            $"rideActive={rideActive} menuVisible={menuVisible} accepted={accepted} " +
            $"mode={_camera?.View.ToString() ?? "uninitialised"}");
#endif
        if (!accepted)
        {
#if DEBUG
            if (!rideActive) GD.Print("[CameraInspect] F6 rejected: no active ride.");
            else if (menuVisible) GD.Print("[CameraInspect] F6 rejected: in-ride menu/modal is open.");
            else GD.Print("[CameraInspect] F6 rejected: key release or echo event.");
#endif
            return;
        }

        var view = CycleCameraInspectionView();
        _hudController?.ShowCameraInspectionView(view);
        GetViewport().SetInputAsHandled();
    }

    private bool IsTypingIntoControl()
    {
        var focus = GetViewport().GuiGetFocusOwner();
        return focus is LineEdit or TextEdit or SpinBox;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut)
        {
            _steerLeftHeld = false;
            _steerRightHeld = false;
            _steering.Update(0, false, false, null, false, paused: true);
        }
    }

#if DEBUG
    private void CycleDebugSceneryIsolation()
    {
        // F7 steps through whole-scene and per-layer views without changing the
        // visibility or processing of the active ride and trainer simulation.
        _debugSceneryIsolation = (_debugSceneryIsolation + 1) % 6;
        var buildings = _debugSceneryIsolation is not (1 or 5);
        var vegetation = _debugSceneryIsolation is not (2 or 5);
        var terrain = _debugSceneryIsolation is not (3 or 5);
        var streets = _debugSceneryIsolation is not (4 or 5);
        if (_buildingLayer is not null) _buildingLayer.Visible = buildings;
        if (_vegetationLayer is not null) _vegetationLayer.Visible = vegetation;
        if (_terrainLayer is not null) _terrainLayer.Visible = terrain;
        if (_backgroundStreetLayer is not null) _backgroundStreetLayer.Visible = streets;
        var label = _debugSceneryIsolation switch
        {
            0 => "all layers",
            1 => "buildings hidden",
            2 => "vegetation hidden",
            3 => "terrain hidden",
            4 => "background streets hidden",
            _ => "road and rider only"
        };
        GD.Print($"[SceneryInspect] F7 view: {label}; press F7 to cycle. " +
            $"rider={_rider.GlobalPosition} camera={_camera.GlobalPosition}");
        LogNearbyScenery();
    }

    private void LogNearbyScenery()
    {
        if (_geometry is null) return;
        var rider = _rider.GlobalPosition;
        var camera = _camera.GlobalPosition;
        const double chunkLength = 350;
        foreach (var building in _debugBuildings.Select(item =>
        {
            var sampledElevations = item.FootprintElevationMeters.Where(value => value.HasValue)
                .Select(value => value!.Value).ToArray();
            var points = item.Footprint.Select((point, index) => _geometry.GetPositionForCoordinate(
                point.Latitude, point.Longitude, item.FootprintElevationMeters.Count == item.Footprint.Count
                    ? item.FootprintElevationMeters[index] ?? item.BaseElevationMeters ?? 0
                    : item.BaseElevationMeters ?? 0)).ToArray();
            var center = points.Aggregate(Vector3.Zero, (sum, point) => sum + new Vector3(point.X, point.Y, point.Z)) / points.Length;
            var min = new Vector3(points.Min(point => point.X), points.Min(point => point.Y), points.Min(point => point.Z));
            var max = new Vector3(points.Max(point => point.X), points.Max(point => point.Y), points.Max(point => point.Z));
            var bounds = new Aabb(min, max - min);
            var distance = Math.Min(center.DistanceTo(rider), center.DistanceTo(camera));
            return (Item: item, Center: center, Bounds: bounds, Distance: distance,
                MinGround: sampledElevations.Length > 0 ? sampledElevations.Min() : item.BaseElevationMeters ?? 0,
                MaxGround: sampledElevations.Length > 0 ? sampledElevations.Max() : item.BaseElevationMeters ?? 0);
        }).Where(item => item.Distance <= 45).OrderBy(item => item.Distance).Take(12))
        {
            GD.Print($"[SceneryInspect] building OSM {building.Item.OsmType}/{building.Item.OsmId} " +
                $"route={building.Item.NearestRouteDistanceMeters:F1}m source=LINZ-footprint:{building.MinGround:F2}..{building.MaxGround:F2}m " +
                $"height={building.Item.HeightMeters:F1}m chunk={Math.Floor(building.Item.NearestRouteDistanceMeters / chunkLength):F0} " +
                $"center={building.Center} bounds={building.Bounds} " +
                $"{_buildingLayer?.DebugChunkBounds((int)Math.Floor(building.Item.NearestRouteDistanceMeters / chunkLength))} " +
                $"riderCameraDistance={building.Distance:F1}m");
        }
        if (_debugVegetation is { } vegetation)
        {
            foreach (var area in vegetation.Areas.Select(item =>
            {
                var points = item.Polygon.Select(point => _geometry.GetPositionForCoordinate(
                    point[1], point[0], item.BaseElevationMeters ?? 0)).ToArray();
                var center = points.Aggregate(Vector3.Zero, (sum, point) => sum + new Vector3(point.X, point.Y, point.Z)) / points.Length;
                var distance = Math.Min(center.DistanceTo(rider), center.DistanceTo(camera));
                var size = new Vector3(points.Max(point => point.X) - points.Min(point => point.X),
                    points.Max(point => point.Y) - points.Min(point => point.Y),
                    points.Max(point => point.Z) - points.Min(point => point.Z));
                return (Item: item, Center: center, Size: size, Distance: distance);
            }).Where(item => item.Distance <= 60).OrderBy(item => item.Distance).Take(12))
                GD.Print($"[SceneryInspect] vegetation area OSM {area.Item.OsmType}/{area.Item.OsmId} " +
                    $"route={area.Item.NearestRouteDistanceMeters:F1}m category={area.Item.Category} " +
                    $"source=LINZ-area-samples:{area.Item.ElevationSampleCount} elevation={area.Item.BaseElevationMeters:F2}m " +
                    $"variation={area.Item.ElevationVariationMeters:F1}m renderGround={area.Item.RenderGround} " +
                    $"center={area.Center} boundsSize={area.Size} riderCameraDistance={area.Distance:F1}m");

        foreach (var tree in vegetation.Trees.Select(item =>
        {
            var point = _geometry.GetPositionForCoordinate(item.Latitude, item.Longitude, item.ElevationMeters ?? 0);
            var distance = Math.Min(new Vector3(point.X, point.Y, point.Z).DistanceTo(rider),
                new Vector3(point.X, point.Y, point.Z).DistanceTo(camera));
            return (Item: item, Position: point, Distance: distance);
        }).Where(item => item.Distance <= 60).OrderBy(item => item.Distance).Take(16))
            GD.Print($"[SceneryInspect] tree OSM {tree.Item.OsmId} route={tree.Item.NearestRouteDistanceMeters:F1}m " +
                $"source=LINZ:{tree.Item.ElevationMeters:F2}m world={tree.Position} " +
                $"bounds=({tree.Item.CanopyRadiusMeters * 2:F1},{tree.Item.HeightMeters:F1},{tree.Item.CanopyRadiusMeters * 2:F1}) " +
                $"riderCameraDistance={tree.Distance:F1}m");
        }
        _terrainLayer?.LogNearbyChunkBounds(rider, camera);
    }
#endif

    public static string FormatCameraView(RiderCameraView view) => view switch
    {
        RiderCameraView.GameplayOriginal => "Gameplay (original)",
        RiderCameraView.GameplayRefined => "Gameplay (refined)",
        RiderCameraView.CloseRear => "Close rear",
        RiderCameraView.RearThreeQuarter => "Rear three-quarter",
        RiderCameraView.Side => "Side",
        _ => "Front three-quarter"
    };

    public override void _Process(double delta)
    {
        if (!_session.IsRideActive || _session.CurrentRoute is null)
        {
            Hide();
            _camera.Current = false;
            if (_wasActive)
            {
                SaveChallengeAttempt();
                _routeLoadAttempt.Reset();
                _steering.Reset();
                _steerLeftHeld = _steerRightHeld = false;
            }
            _wasActive = false;
            return;
        }
        var rideJustActivated = !_wasActive;
        if (_routeLoadAttempt.TryBegin(_session.CurrentRoute))
        {
            _wasActive = true;
            var timer = Stopwatch.StartNew();
            try
            {
                LoadRoute(_session.CurrentRoute);
                _routeLoadAttempt.MarkLoaded(_session.CurrentRoute);
                _camera.ResetFollow();
                timer.Stop();
                GameLogger.Log("visual_route_load_timing", new
                {
                    routeId = _session.CurrentRoute.Id,
                    state = _routeLoadAttempt.State.ToString(),
                    totalMilliseconds = timer.Elapsed.TotalMilliseconds,
                    buildings = _buildingLayer?.BuildingsLoaded ?? 0,
                    buildingTriangles = _buildingLayer?.TriangleCount ?? 0,
                    backgroundStreets = _backgroundStreetLayer?.LoadedStreetCount ?? 0,
                    backgroundStreetTriangles = _backgroundStreetLayer?.TriangleCount ?? 0,
                    terrainTriangles = _terrainLayer?.TerrainTriangleCount ?? 0
                });
            }
            catch (Exception ex)
            {
                timer.Stop();
                _routeLoadAttempt.MarkFailed(_session.CurrentRoute, ex.Message);
                CleanupFailedRouteLoad();
                var message = $"Could not load the world for { _session.CurrentRoute.Name } ({_session.CurrentRoute.Id}): {ex.Message}";
                GD.PushError($"[WorldLoad] {message}");
                GameLogger.Log("visual_route_load_failed", new
                {
                    routeId = _session.CurrentRoute.Id,
                    exception = ex.ToString(),
                    totalMilliseconds = timer.Elapsed.TotalMilliseconds
                });
                Hide();
                _camera.Current = false;
                _ = _session.StopRideAfterWorldLoadFailureAsync(message);
                return;
            }
        }
        if (_routeLoadAttempt.State == RouteWorldLoadState.Failed)
        {
            Hide();
            _camera.Current = false;
            return;
        }
        if (_routeLoadAttempt.State != RouteWorldLoadState.Loaded) return;
        _wasActive = true;
        Show();
        _camera.Current = true;
        if (rideJustActivated)
        {
            _gameFeatureToggledDuringAttempt = false;
            _lastChallengeDistance = 0;
            _lastChallengeElapsed = 0;
        }
#if DEBUG
        if (rideJustActivated)
            GD.Print($"[CameraInspect] ride active route={_session.CurrentRoute.Id} node={GetPath()} " +
                $"visible={IsVisibleInTree()} inputProcessing={IsProcessingInput()} mode={_camera.View} camera={_camera.GetPath()}");
#endif
        var distance = _session.CurrentRideState.DistanceMeters;
        _lastChallengeDistance = distance;
        _lastChallengeElapsed = _session.CurrentRideState.ElapsedSeconds;
        var pose = _geometry!.GetPoseAtDistance(distance);
        _lastPose = pose;
        var rideState = _session.CurrentRideState;
        if (_session.IsRidePaused || _rideMenuPanel?.IsMenuVisible == true)
        {
            _steerLeftHeld = false;
            _steerRightHeld = false;
            _keyboardControllerSampleBaseline = _deviceConnectionPanel?.LatestSteeringInputAt ?? _lastSteeringSampleAt;
        }
        var sterzoAngle = 0f;
        var sterzoTimestamp = DateTimeOffset.MinValue;
        var hasFreshSterzo = _deviceConnectionPanel is not null && _deviceConnectionPanel.TryGetFreshSteeringAngle(out sterzoAngle, out sterzoTimestamp);
        var freshSterzoSample = hasFreshSterzo && sterzoTimestamp > _lastSteeringSampleAt;
        if (freshSterzoSample) _lastSteeringSampleAt = sterzoTimestamp;
        var lateralOffset = _steering.Update(delta, _steerLeftHeld, _steerRightHeld,
            hasFreshSterzo ? sterzoAngle : null, hasFreshSterzo, _session.IsRidePaused,
            IsTypingIntoControl(), GameBootstrap.Settings.Steering.Sensitivity,
            GameBootstrap.Settings.Steering.DeadZoneDegrees, ProceduralRoad.WidthMeters,
            freshSterzoSample && sterzoTimestamp > _keyboardControllerSampleBaseline);
        _steeringDiagnosticElapsed += delta;
        if (_steeringDiagnosticElapsed >= 1)
        {
            _steeringDiagnosticElapsed = 0;
            GameLogger.Log("steering_sample", new { routeId = _loadedRoute?.Id, lateralOffsetMeters = lateralOffset,
                keyboardLeft = _steerLeftHeld, keyboardRight = _steerRightHeld, sterzoConnected = _deviceConnectionPanel?.HasSteeringController == true,
                sterzoFresh = hasFreshSterzo, paused = _session.IsRidePaused });
        }
        var gameEnabled = GameFeaturesPreference.Enabled;
        if (gameEnabled) EnsureCollectibleLayer();
        _collectibleLayer?.Attempt.MarkPractice(toggled: _gameFeatureToggledDuringAttempt);
        _collectibleLayer?.UpdateRide(distance, lateralOffset, gameEnabled,
            _loadedRoute?.RouteType == "Loop");
        _hudController?.SetGameScore(gameEnabled, _collectibleLayer?.Score ?? 0,
            _collectibleLayer?.Collected ?? 0, _collectibleLayer?.Available ?? 0);
        _hudController?.SetGameSteeringHint(gameEnabled && _deviceConnectionPanel?.HasSteeringController != true);
        var routeLength = _geometry.LengthMeters;
        var wrappedDistance = _geometry.WrapDistance(distance);
        var playerState = new RiderState("player", "You", _loadedRoute!.Id, distance, wrappedDistance,
            (int)Math.Floor(distance / routeLength) + 1, lateralOffset,
            Math.Max(0, rideState.VirtualSpeedKph) / 3.6, rideState.CurrentPowerWatts,
            _session.CurrentAnimationCadenceRpm, _session.IsRidePaused, rideState.ElapsedSeconds);
        _riderStateSource!.Update(playerState, routeLength, _loadedRoute.GetGradientAt, delta);
        _session.GhostRider?.Update(rideState.ElapsedSeconds, routeLength, _session.IsRidePaused);
        _session.UpdateGhostIndicator(distance);
        foreach (var riderState in _riderStateSource.GetRiders().Concat(_session.GhostRider?.GetRiders() ?? Array.Empty<RiderState>()))
        {
            if (!_riderVisuals.TryGetValue(riderState.RiderId, out var visual))
            {
                if (riderState.RiderId != "ghost") continue;
                visual = new CyclistVisual { Name = "GhostCyclist" };
                visual.Build(5);
                (_aiVisualRoot ?? this).AddChild(visual);
                _riderVisuals["ghost"] = visual;
            }
            var riderPose = _geometry.GetPoseAtDistance(riderState.WrappedRouteDistanceMeters);
            visual.UpdateFromRiderState(riderState, riderPose, _loadedRoute.GetGradientAt(riderState.WrappedRouteDistanceMeters), delta);
        }
        UpdateRemoteRiders(delta, routeLength);
        var position = _rider.GlobalPosition;
        var forward = ProceduralRoad.ToGodot(pose.Forward);
        _camera.Follow(position, forward, delta);
        _landmarkLayer?.UpdateForRideDistance(distance);
        _buildingLayer?.UpdateForRideDistance(distance);
        _vegetationLayer?.UpdateForRideDistance(distance);
        _streetFurnitureLayer?.UpdateForRideDistance(distance);
        _roadMarkingLayer?.UpdateForRideDistance(distance);
        _backgroundStreetLayer?.UpdateForRideDistance(distance);
        _terrainLayer?.UpdateForRideDistance(distance);
        _ambientActorManager?.UpdateForRider(new System.Numerics.Vector3(position.X, position.Y, position.Z),
            delta, _session.IsRidePaused);
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

    private void EnsureCollectibleLayer()
    {
        if (_collectibleLayer is not null || _routeRoot is null || _loadedRoute is null || _geometry is null) return;
        _collectibleLayer = new CollectibleCatLayer { Name = "CollectibleCats" };
        _routeRoot.AddChild(_collectibleLayer);
        _collectibleLayer.Configure(_loadedRoute.Id, _geometry);
        GameLogger.Log("collectible_layout_created", new { routeId = _loadedRoute.Id,
            challengeVersion = IndoorCycling.Core.Simulation.CollectibleChallenge.CurrentVersion,
            placementCount = _collectibleLayer.Available, routeFingerprint = _loadedRoute.GeometryFingerprint });
        _collectibleLayer.CollectedPickup += pickup =>
        {
            var kind = pickup.Kind == IndoorCycling.Core.Simulation.CollectibleKind.GoldenCat ? "Golden" : "Blue";
            _hudController?.ShowCatCollection(kind, pickup.Points, _collectibleLayer.Score);
            GameLogger.Log("collectible_collected", new { pickup.Id, pickup.Kind, pickup.Points, score = _collectibleLayer.Score });
        };
    }

    public void OnGameFeaturesChanged(bool enabled)
    {
        if (_session?.IsRideActive == true)
        {
            _gameFeatureToggledDuringAttempt = true;
            _collectibleLayer?.Attempt.MarkPractice(toggled: true);
            GameLogger.Log("game_features_changed_during_ride", new { enabled, ineligible = true });
        }
        if (!enabled)
        {
            _collectibleLayer?.Hide();
            _hudController?.SetGameScore(false, 0, 0, 0);
        }
    }

    public void MarkChallengePractice(bool debugJump = false, bool catchUp = false)
    {
        _collectibleLayer?.Attempt.MarkPractice(debugJump: debugJump, catchUp: catchUp);
        _collectibleLayer?.SuppressNextSweep();
    }

    private void SaveChallengeAttempt()
    {
        LastChallengeSummary = null;
        if (_collectibleLayer is null || _loadedRoute is null) return;
        var length = _loadedRoute.DistanceMeters;
        var completed = length > 0 && _lastChallengeDistance >= length && !_gameFeatureToggledDuringAttempt;
        _collectibleLayer.Attempt.MarkPractice(
            dummy: GameBootstrap.TrainerClient is IndoorCycling.Trainer.Implementations.FakeTrainerClient,
            toggled: _gameFeatureToggledDuringAttempt);
        var lap = Math.Max(1, (int)Math.Floor(_lastChallengeDistance / Math.Max(1, length)));
        var result = _collectibleLayer.Attempt.CreateResult(_loadedRoute.Id, _loadedRoute.GeometryFingerprint,
            IndoorCycling.Core.Simulation.CollectibleChallenge.CurrentVersion, lap,
            _collectibleLayer.Available * lap, completed, _lastChallengeElapsed);
        if (GameBootstrap.Settings.ActiveProfileId is { } profileId)
        {
            try
            {
                var isBest = CollectibleResultStore.Save(profileId, result);
                if (GameFeaturesPreference.Enabled)
                    LastChallengeSummary = CollectibleUiText.ResultSummary(result.Score, result.PickupsCollected,
                        result.PickupsAvailable, (result.Completed ? "completed" : "partial") + (isBest ? " · personal best" : "") +
                        (result.EligibleForPersonalBest ? "" : " · practice attempt"));
                GameLogger.Log("collectible_attempt_saved", new { result.RouteId, result.Score, result.PickupsCollected,
                    result.PickupsAvailable, result.Completed, result.EligibleForPersonalBest, personalBest = isBest });
            }
            catch (Exception ex) { GameLogger.Log("collectible_attempt_save_failed", new { message = ex.Message }); }
        }
    }

    private void UpdateRemoteRiders(double delta, double routeLength)
    {
        if (_geometry is null || _loadedRoute is null || routeLength <= 0) return;
        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        var localDistance = _session.CurrentRideState.DistanceMeters;
        foreach (var remote in _session.GetInterpolatedRemoteRiders())
        {
            if (remote.DistanceMeters is not { } absoluteDistance) continue;
            activeIds.Add(remote.RiderId);
            var wrapped = ((absoluteDistance % routeLength) + routeLength) % routeLength;
            var relativeDistance = MultiplayerRoutePosition.SignedWrappedSeparation(absoluteDistance, localDistance, routeLength);
            var nearbyDistance = Math.Abs(relativeDistance);
            if (nearbyDistance > RemoteVisualRadiusMeters)
            {
                if (_remoteVisuals.TryGetValue(remote.RiderId, out var hiddenVisual) && hiddenVisual.Visible)
                {
                    hiddenVisual.Visible = false;
                    LogRemoteRiderDiagnostic("multiplayer_remote_visual_left_range", new
                    {
                        riderIdentityHash = EosIdentityDiagnostics.HashProductUserId(remote.RiderId),
                        relativeDistanceMeters = relativeDistance,
                        visualRangeMeters = RemoteVisualRadiusMeters
                    });
                }
                continue;
            }
            if (!_remoteVisuals.TryGetValue(remote.RiderId, out var visual))
            {
                visual = new CyclistVisual { Name = "RemoteCyclist_" + remote.RiderId };
                visual.Build(StringComparer.Ordinal.GetHashCode(remote.RiderId) & int.MaxValue);
                visual.Visible = false;
                var name = new Label3D
                {
                    Name = "RiderName",
                    Text = remote.DisplayName,
                    FontSize = 36,
                    PixelSize = .0055f,
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    NoDepthTest = true,
                    Modulate = new Color(.95f, .87f, .70f, .95f),
                    OutlineSize = 6,
                    OutlineModulate = new Color(.08f, .07f, .06f, .9f),
                    Position = new Vector3(0, 2.05f, 0)
                };
                visual.AddChild(name);
                (_remoteVisualRoot ?? this).AddChild(visual);
                _remoteVisuals.Add(remote.RiderId, visual);
                _remoteNameLabels.Add(remote.RiderId, name);
                LogRemoteRiderDiagnostic("multiplayer_remote_visual_created", new { riderIdentityHash = EosIdentityDiagnostics.HashProductUserId(remote.RiderId), name = remote.DisplayName });
            }
            if (!visual.Visible)
                LogRemoteRiderDiagnostic("multiplayer_remote_visual_entered_range", new { riderIdentityHash = EosIdentityDiagnostics.HashProductUserId(remote.RiderId), relativeDistanceMeters = relativeDistance });
            visual.Visible = true;
            if (_remoteNameLabels.TryGetValue(remote.RiderId, out var label))
            {
                label.Text = remote.DisplayName;
                var alpha = (float)Math.Clamp(1.0 - Math.Max(0, nearbyDistance - 90) / 260.0, .18, .94);
                label.Modulate = new Color(.95f, .87f, .70f, alpha);
            }
            var pose = _geometry.GetPoseAtDistance(wrapped);
            var rider = new RiderState(remote.RiderId, remote.DisplayName, _loadedRoute.Id, absoluteDistance, wrapped,
                (int)Math.Floor(absoluteDistance / routeLength) + 1, remote.LateralOffsetMeters ?? RiderLaneMath.RiderLateralOffsetMeters,
                (remote.Paused ? 0 : remote.SpeedKph ?? 0) / 3.6, remote.PowerWatts ?? 0,
                remote.CadenceRpm, remote.Paused, 0);
            visual.UpdateFromRiderState(rider, pose, _loadedRoute.GetGradientAt(wrapped), delta);
        }
        foreach (var id in _remoteVisuals.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            var visual = _remoteVisuals[id];
            _remoteVisuals.Remove(id);
            _remoteNameLabels.Remove(id);
            visual.QueueFree();
            LogRemoteRiderDiagnostic("multiplayer_remote_visual_removed", new
            {
                riderIdentityHash = EosIdentityDiagnostics.HashProductUserId(id),
                reason = _session.MultiplayerRiders.Any(rider => rider.RiderId == id) ? "stale_or_no_render_sample" : "left_or_disconnected"
            });
        }
    }

    private static void LogRemoteRiderDiagnostic(string eventName, object details)
    {
#if DEBUG
        if (GameBootstrap.DebugModeEnabled) GameLogger.Log(eventName, details);
#endif
    }

    public void ResetAfterReposition()
    {
        _rider?.ResetVisualInterpolation();
        _camera?.ResetFollow();
        if (_geometry is null || _loadedRoute is null) return;
        var distance = _session.CurrentRideState.DistanceMeters;
        var wrapped = _geometry.WrapDistance(distance);
        var pose = _geometry.GetPoseAtDistance(distance);
        _lastPose = pose;
        var state = _session.CurrentRideState;
        var rider = new RiderState("player", "You", _loadedRoute.Id, distance, wrapped,
            (int)Math.Floor(distance / _geometry.LengthMeters) + 1, _steering.LateralOffsetMeters,
            0, state.CurrentPowerWatts, _session.CurrentAnimationCadenceRpm, _session.IsRidePaused, state.ElapsedSeconds);
        _rider!.UpdateFromRiderState(rider, pose, _loadedRoute.GetGradientAt(wrapped), 0);
        _camera!.Follow(_rider.GlobalPosition, ProceduralRoad.ToGodot(pose.Forward), 1);
        _landmarkLayer?.UpdateForRideDistance(distance);
        _buildingLayer?.UpdateForRideDistance(distance);
        _vegetationLayer?.UpdateForRideDistance(distance);
        _streetFurnitureLayer?.UpdateForRideDistance(distance);
        _roadMarkingLayer?.UpdateForRideDistance(distance);
        _backgroundStreetLayer?.UpdateForRideDistance(distance);
        _terrainLayer?.UpdateForRideDistance(distance);
    }

    public RideWorldDiagnostic GetDiagnosticState()
    {
        var forward = ProceduralRoad.ToGodot(_lastPose.Forward);
        var distance = _session.CurrentRideState.DistanceMeters;
        var landmarks = _landmarkLayer?.GetDebugState(distance, _rider.GlobalPosition) ?? default;
        var nearestAi = GetNearestAiDebugStates();
        var nearestAiRider = nearestAi.FirstOrDefault();
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
            _roadMarkingLayer?.NearbyCrossingCount ?? 0,
            _ambientActorManager?.ActiveCarCount ?? 0,
            _ambientActorManager?.ActivePedestrianCount ?? 0,
            _ambientActorManager?.CarPathCount ?? 0, _ambientActorManager?.PedestrianPathCount ?? 0,
            _ambientActorManager?.CarSpawnAttempts ?? 0, _ambientActorManager?.PedestrianSpawnAttempts ?? 0,
            _ambientActorManager?.CarOutsideSpawnRadius ?? 0, _ambientActorManager?.PedestrianOutsideSpawnRadius ?? 0,
            _ambientActorManager?.CarTooCloseToRider ?? 0, _ambientActorManager?.PedestrianTooCloseToRider ?? 0,
            _ambientActorManager?.CarNoEligiblePath ?? 0, _ambientActorManager?.PedestrianNoValidPath ?? 0,
            _ambientActorManager?.CarInvalidTransform ?? 0, _ambientActorManager?.PedestrianInvalidTransform ?? 0,
            _ambientActorManager?.CarIllegalOnewayDirections ?? 0,
            _ambientActorManager?.TotalStreetPolylines ?? 0, _ambientActorManager?.EligibleStreetPolylines ?? 0,
            _ambientActorManager?.InvalidOrShortCarPaths ?? 0, _ambientActorManager?.InvalidOrShortPedestrianPaths ?? 0,
            _ambientActorManager?.PedestrianSeamOrEndSamplesExcluded ?? 0,
            AmbientPosition(_ambientActorManager?.FirstCarState), AmbientDistance(_ambientActorManager?.FirstCarState),
            _ambientActorManager?.FirstCarRoadName, _ambientActorManager?.FirstCarRoadClass,
            _ambientActorManager?.FirstCarState?.SpeedMetersPerSecond, _ambientActorManager?.FirstCarVisible,
            AmbientPosition(_ambientActorManager?.FirstPedestrianState), AmbientDistance(_ambientActorManager?.FirstPedestrianState),
            _ambientActorManager?.FirstPedestrianState?.SpeedMetersPerSecond, _ambientActorManager?.FirstPedestrianVisible,
            Math.Max(0, ( _riderStateSource?.GetRiders().Count ?? 1) - 1),
            nearestAiRider?.Name, nearestAiRider?.RelativeDistanceMeters, nearestAiRider?.SpeedKph, nearestAi,
            GetRemoteHumanRiderDiagnostics(distance, routeLength: _geometry?.LengthMeters ?? 0));
    }

    private IReadOnlyList<RemoteHumanRiderDebugState> GetRemoteHumanRiderDiagnostics(double localDistance, double routeLength)
    {
        var geometry = _geometry;
        if (geometry is null || _loadedRoute is null || routeLength <= 0)
            return Array.Empty<RemoteHumanRiderDebugState>();
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        return _session.MultiplayerRiders.Select(remote =>
        {
            var routeDistance = remote.DistanceMeters;
            var hasState = remote.HasState && routeDistance.HasValue;
            var relative = hasState
                ? MultiplayerRoutePosition.SignedWrappedSeparation(routeDistance!.Value, localDistance, routeLength)
                : (double?)null;
            var hasVisual = _remoteVisuals.TryGetValue(remote.RiderId, out var visual);
            var visualValid = hasVisual && visual is not null && GodotObject.IsInstanceValid(visual);
            var visible = visualValid && visual!.Visible;
            var age = hasState ? Math.Max(0, now - remote.ReceivedAtSeconds) : (double?)null;
            bool? behind = null;
            bool? inFrustum = null;
            if (hasState && relative is { } signed)
            {
                var wrapped = ((routeDistance!.Value % routeLength) + routeLength) % routeLength;
                var position = visualValid
                    ? visual!.GlobalPosition
                    : ProceduralRoad.ToGodot(geometry.GetPoseAtDistance(wrapped).Position);
                behind = _camera.IsPositionBehind(position);
                inFrustum = _camera.IsPositionInFrustum(position);
            }
            return new RemoteHumanRiderDebugState(remote.RiderId, remote.DisplayName, hasState,
                relative, age, hasVisual, visible, relative is { } distance && Math.Abs(distance) <= RemoteVisualRadiusMeters,
                behind, inFrustum);
        }).OrderBy(rider => rider.RelativeDistanceMeters is { } relative ? Math.Abs(relative) : double.PositiveInfinity)
            .Take(5).ToArray();
    }

    private IReadOnlyList<AiRiderDebugState> GetNearestAiDebugStates()
    {
        if (_riderStateSource is null || _geometry is null) return Array.Empty<AiRiderDebugState>();
        var riders = _riderStateSource.GetRiders();
        var player = riders.FirstOrDefault(rider => rider.RiderId == "player");
        if (player is null) return Array.Empty<AiRiderDebugState>();
        return riders.Where(rider => rider.RiderId != "player")
            .Select(rider => new AiRiderDebugState(rider.DisplayName,
                AiRiderPopulation.RelativeDistance(rider.WrappedRouteDistanceMeters,
                    player.WrappedRouteDistanceMeters, _geometry.LengthMeters),
                rider.RouteDistanceMeters, rider.VirtualSpeedMps * 3.6,
                rider.PowerWatts, rider.CadenceRpm))
            .OrderBy(rider => Math.Abs(rider.RelativeDistanceMeters)).Take(3).ToArray();
    }

    private Godot.Vector3? AmbientPosition(AmbientActorState? state) => state is { } value
        ? new Godot.Vector3(value.WorldPosition.X, value.WorldPosition.Y, value.WorldPosition.Z) : null;
    private double? AmbientDistance(AmbientActorState? state) => state is { } value
        ? System.Numerics.Vector3.Distance(value.WorldPosition,
            new System.Numerics.Vector3(_rider.GlobalPosition.X, _rider.GlobalPosition.Y, _rider.GlobalPosition.Z)) : null;

    private void LoadRoute(RouteDefinition route)
    {
        _collectibleLayer = null;
        if (_remoteVisualRoot is not null)
        {
            RemoveChild(_remoteVisualRoot);
            _remoteVisualRoot.QueueFree();
            _remoteVisualRoot = null;
        }
        _remoteVisuals.Clear();
        _remoteNameLabels.Clear();
        if (_aiVisualRoot is not null)
        {
            RemoveChild(_aiVisualRoot);
            _aiVisualRoot.QueueFree();
            _aiVisualRoot = null;
        }
        _riderVisuals.Clear();
        _riderVisuals["player"] = _rider;
        _aiPopulation = new AiRiderPopulation(AiRiderTargetCount, AiRiderMaximumCount, AiRiderRandomSeed);
        _riderStateSource = new LocalRiderStateSource(_aiPopulation);
        _aiVisualRoot = new Node3D { Name = "AiRiderVisuals" };
        AddChild(_aiVisualRoot);
        _remoteVisualRoot = new Node3D { Name = "RemoteRiderVisuals" };
        AddChild(_remoteVisualRoot);
        for (var i = 0; i < _aiPopulation.Controllers.Count; i++)
        {
            var controller = _aiPopulation.Controllers[i];
            var aiVisual = new CyclistVisual { Name = $"AiCyclist{i + 1:D2}_{controller.Profile.DisplayName}" };
            aiVisual.Build(i + 1);
            _aiVisualRoot.AddChild(aiVisual);
            _riderVisuals[controller.RiderId] = aiVisual;
        }
        if (_routeRoot is not null)
        {
            RemoveChild(_routeRoot);
            _routeRoot.QueueFree();
        }
        _geometry = new VisualRoute(route);
        var groundPlaneY = _geometry.GroundPlaneY;
        _routeRoot = new Node3D { Name = "RouteGeometry" };
        AddChild(_routeRoot);
        var asphalt = WodraWorldStyle.Asphalt;
        _routeRoot.AddChild(ProceduralRoad.BuildRibbon(_geometry, ProceduralRoad.WidthMeters, 0, asphalt));
        var junctionFill = ProceduralRoad.BuildSharpJoinFills(_geometry, ProceduralRoad.WidthMeters, asphalt);
        if (junctionFill is not null) _routeRoot.AddChild(junctionFill);
        _routeRoot.AddChild(ProceduralRoad.BuildRibbon(_geometry, 0.12f, 0.015f, new Color(0.9f, 0.82f, 0.35f)));
        foreach (float offset in new[] { -3.25f, 3.25f })
            _routeRoot.AddChild(ProceduralRoad.BuildRibbon(_geometry, 0.12f, 0.02f, Colors.White, offset));
        var assembly = typeof(RouteCatalog).Assembly;
        var resourcePrefix = $"Wodra.GameData.Routes.{route.Id}.";
        RouteBackgroundStreetData? backgroundStreetData = null;
        try
        {
            backgroundStreetData = RouteBackgroundStreetDataLoader.TryLoadEmbedded(assembly,
                resourcePrefix + "streets.json", route.Id);
        }
        catch (Exception ex)
        {
            ReportOptionalLayerFailure(route, "background street sidecar", ex);
        }
        var routeSpatialIndex = new BackgroundStreetGeometry.RouteSegmentSpatialIndex(_geometry.Samples);
        IReadOnlyList<RoadJunctionOpening> junctionOpenings = [];
        if (backgroundStreetData is not null)
        {
            try
            {
                junctionOpenings = BackgroundStreetGeometry.BuildJunctionOpenings(_geometry, backgroundStreetData, routeSpatialIndex);
            }
            catch (Exception ex)
            {
                backgroundStreetData = null;
                ReportOptionalLayerFailure(route, "background street junction connections", ex);
            }
        }
        _routeRoot.AddChild(ProceduralRoad.BuildKerbsAndFootpaths(_geometry,true,true,junctionOpenings));
        // Route data has no road-class evidence for continuous delineators.
        // Render only explicit OSM furniture from StreetFurnitureLayer below.
        _landmarkLayer = null;
        _buildingLayer = null;
        _vegetationLayer = null;
        _streetFurnitureLayer = null;
        _roadMarkingLayer = null;
        _backgroundStreetLayer = null;
        _ambientActorManager = null;
        _terrainLayer = null;
        RouteVegetationData? vegetationData = null;
        {
            var streets=backgroundStreetData;
            TerrainRoadCorridor[] terrainRoads = [];
            if (streets is not null)
            {
                try { terrainRoads = BackgroundStreetGeometry.BuildTerrainCorridors(_geometry, streets, routeSpatialIndex); }
                catch (Exception ex)
                {
                    streets = null;
                    ReportOptionalLayerFailure(route, "background street terrain cutouts", ex);
                }
            }
            var terrain = new TerrainLayer { Name = "LINZTerrain" };
            if (terrain.Configure(_geometry, resourcePrefix + "terrain.json", route.Id,terrainRoads))
            {
                _terrainLayer = terrain;
                _routeRoot.AddChild(terrain);
                _terrainLayer.UpdateForRideDistance(_session.CurrentRideState.DistanceMeters);
            }
            // Keep the old-route fallback only when a sampled terrain sidecar is
            // unavailable. A 60 m ribbon at route elevation is a false ground
            // surface on routes that already have real DEM terrain: it can cut
            // across hills, mapped streets, cars, and roadside object bases.
            if (RouteGroundFallbackPolicy.ShouldAddLegacyApron(_terrainLayer is not null))
                _routeRoot.AddChild(ProceduralRoad.BuildRibbon(_geometry, 60, -0.08f,
                    WodraWorldStyle.Grass.Darkened(0.08f)));
            var originElevation = route.Points?.FirstOrDefault()?.ElevationMeters ?? 0;
            IReadOnlyList<RouteBuildingData> buildings = [];
            var buildingResource = resourcePrefix + "buildings.json";
            if (RoutePackageFiles.Exists(assembly, buildingResource, route.Id))
            {
                buildings = RouteBuildingDataLoader.LoadEmbedded(assembly, buildingResource, route.Id);
#if DEBUG
                _debugBuildings = buildings;
#endif
                if (buildings.Count > 0)
                    groundPlaneY = Math.Min(groundPlaneY,
                        (float)(buildings.Min(item => item.BaseElevationMeters!.Value - originElevation) - VisualRoute.GroundClearanceMeters));
                _buildingLayer = new BuildingLayer { Name = "RouteBuildings" };
                _routeRoot.AddChild(_buildingLayer);
                _buildingLayer.Configure(_geometry, route, buildings);
                _buildingLayer.UpdateForRideDistance(_session.CurrentRideState.DistanceMeters);
            }
            var landmarkResource = resourcePrefix + "landmarks.json";
            if (RoutePackageFiles.Exists(assembly, landmarkResource, route.Id))
            {
                var landmarks = RouteLandmarkDataLoader.LoadEmbedded(assembly, landmarkResource, route.Id);
                _landmarkLayer = new LandmarkLayer { Name = "RouteLandmarks" };
                _routeRoot.AddChild(_landmarkLayer);
                _landmarkLayer.Configure(_geometry, route, landmarks, buildings);
                _landmarkLayer.UpdateForRideDistance(_session.CurrentRideState.DistanceMeters);
            }
            var vegetationResource = resourcePrefix + "vegetation.json";
            if (RoutePackageFiles.Exists(assembly, vegetationResource, route.Id))
            {
                vegetationData = RouteVegetationDataLoader.LoadEmbedded(assembly, vegetationResource, route.Id);
#if DEBUG
                _debugVegetation = vegetationData;
#endif
                var renderableAreas = vegetationData.Areas.Where(item => item.RenderGround && item.BaseElevationMeters is not null).ToArray();
                if (renderableAreas.Length > 0)
                    groundPlaneY = Math.Min(groundPlaneY,
                        (float)(renderableAreas.Min(item => item.BaseElevationMeters!.Value - originElevation) - VisualRoute.GroundClearanceMeters));
                _vegetationLayer = new VegetationLayer { Name = "RouteVegetation" };
                _routeRoot.AddChild(_vegetationLayer);
                _vegetationLayer.Configure(_geometry, route, vegetationData, terrainRoads);
                _vegetationLayer.UpdateForRideDistance(_session.CurrentRideState.DistanceMeters);
            }
            var furnitureResource = resourcePrefix + "street-furniture.json";
            if (RoutePackageFiles.Exists(assembly, furnitureResource, route.Id))
            {
                var furniture = RouteStreetFurnitureDataLoader.LoadEmbedded(assembly, furnitureResource, route.Id);
                _streetFurnitureLayer = new StreetFurnitureLayer { Name = "RouteStreetFurniture" };
                _routeRoot.AddChild(_streetFurnitureLayer);
                _streetFurnitureLayer.Configure(_geometry, route, furniture);
                _streetFurnitureLayer.UpdateForRideDistance(_session.CurrentRideState.DistanceMeters);
                _roadMarkingLayer = new RoadMarkingLayer { Name = "RoadMarkings" };
                _routeRoot.AddChild(_roadMarkingLayer);
                _roadMarkingLayer.Configure(_geometry, furniture);
                _roadMarkingLayer.UpdateForRideDistance(_session.CurrentRideState.DistanceMeters);
            }
            if(streets is not null)
            {
                try
                {
                    _backgroundStreetLayer=new BackgroundStreetLayer{Name="BackgroundStreets"};
                    _routeRoot.AddChild(_backgroundStreetLayer);
                    _backgroundStreetLayer.Configure(_geometry,streets,routeSpatialIndex);
                    _backgroundStreetLayer.UpdateForRideDistance(_session.CurrentRideState.DistanceMeters);
                    _ambientActorManager = new AmbientActorManager { Name = "AmbientActors", RandomSeed = AmbientActorTuning.DefaultSeed };
                    _routeRoot.AddChild(_ambientActorManager);
                    _ambientActorManager.Configure(_geometry, streets);
                }
                catch (Exception ex)
                {
                    if (_backgroundStreetLayer is not null)
                    {
                        _routeRoot.RemoveChild(_backgroundStreetLayer);
                        _backgroundStreetLayer.QueueFree();
                        _backgroundStreetLayer = null;
                    }
                    if (_ambientActorManager is not null)
                    {
                        _routeRoot.RemoveChild(_ambientActorManager);
                        _ambientActorManager.QueueFree();
                        _ambientActorManager = null;
                    }
                    ReportOptionalLayerFailure(route, "background streets and ambient actors", ex);
                }
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
             backgroundStreetRouteConnections = junctionOpenings.Count,
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
            cyclistApproximateTriangles = _rider.ApproximateTriangleCount,
            aiRiders = _riderVisuals.Count - 1,
            aiCyclistNodesPerRider = _riderVisuals.Values.FirstOrDefault(visual => !ReferenceEquals(visual, _rider))?.ApproximateNodeCount ?? 0,
            aiCyclistTotalNodes = _riderVisuals.Where(pair => pair.Key != "player").Sum(pair => pair.Value.ApproximateNodeCount),
            aiCyclistApproximateTriangles = _riderVisuals.Where(pair => pair.Key != "player").Sum(pair => pair.Value.ApproximateTriangleCount)
        });
        _loadedRoute = route;
        if (GameFeaturesPreference.Enabled) EnsureCollectibleLayer();
    }

    private void CleanupFailedRouteLoad()
    {
        if (_collectibleLayer is not null) { _collectibleLayer.QueueFree(); _collectibleLayer = null; }
        if (_remoteVisualRoot is not null)
        {
            RemoveChild(_remoteVisualRoot);
            _remoteVisualRoot.QueueFree();
            _remoteVisualRoot = null;
        }
        _remoteVisuals.Clear();
        _remoteNameLabels.Clear();
        if (_routeRoot is not null)
        {
            RemoveChild(_routeRoot);
            _routeRoot.QueueFree();
            _routeRoot = null;
        }
        if (_aiVisualRoot is not null)
        {
            RemoveChild(_aiVisualRoot);
            _aiVisualRoot.QueueFree();
            _aiVisualRoot = null;
        }
        _geometry = null;
        _loadedRoute = null;
        _riderStateSource = null;
        _aiPopulation = null;
        _riderVisuals.Clear();
        _landmarkLayer = null;
        _buildingLayer = null;
        _vegetationLayer = null;
        _streetFurnitureLayer = null;
        _roadMarkingLayer = null;
        _backgroundStreetLayer = null;
        _ambientActorManager = null;
        _terrainLayer = null;
    }

    private void ReportOptionalLayerFailure(RouteDefinition route, string layer, Exception exception)
    {
        var message = $"Optional {layer} could not be built for {route.Name} ({route.Id}); the route road and rider remain available. Details: {exception.Message}";
        GD.PushWarning($"[WorldLoad] {message}");
        GameLogger.Log("visual_route_optional_layer_failed", new
        {
            routeId = route.Id,
            layer,
            exception = exception.ToString()
        });
        _hudController?.SetStatus(message);
    }
}
