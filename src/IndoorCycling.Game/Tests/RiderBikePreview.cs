using System;
using System.Linq;
using Godot;

/// <summary>Owner-facing visual preview using the production rider, route, scenery, and camera classes.</summary>
public partial class RiderBikePreview : Node3D
{
    private readonly CyclistVisual[] _riders = new CyclistVisual[3];
    private readonly ChaseCamera _camera = new() { Name = "PreviewChaseCamera" };
    private readonly RiderCameraView[] _views =
    [
        RiderCameraView.GameplayRefined,
        RiderCameraView.Side,
        RiderCameraView.FrontThreeQuarter
    ];
    private VisualRoute _route = null!;
    private RouteDefinition _definition = null!;
    private BuildingLayer _buildings = null!;
    private TerrainLayer _terrain = null!;
    private VegetationLayer _vegetation = null!;
    private double _elapsed;
    private double _routeDistance = 310;
    private double _statsElapsed;
    private double _frameTimeSum;
    private int _frameSamples;
    private float _slowestFrameMs;
    private bool _captureRequested;
    private bool _chaseCaptured;
    private bool _sideCaptured;
    private bool _frontCaptured;

    public override void _Ready()
    {
        try
        {
            _captureRequested = OS.GetCmdlineUserArgs().Contains("--capture-preview");
            BuildWorld();
            for (var i = 0; i < _riders.Length; i++)
            {
                _riders[i] = new CyclistVisual { Name = $"PreviewRider{i + 1}" };
                _riders[i].Build(i);
                AddChild(_riders[i]);
            }
            AddChild(_camera);
            _camera.SetView(RiderCameraView.GameplayRefined);
            AddChild(CreateHud());
            GD.Print("[RiderBikePreview] Wodra route render active · 3 riders · cadences 55/85/115 rpm · camera cycles");
        }
        catch (Exception exception)
        {
            GD.PushError($"[RiderBikePreview] {exception}");
        }
    }

    public override void _Process(double delta)
    {
        if (_route is null || _riders[0] is null) return;
        _elapsed += delta;
        _statsElapsed += delta;
        _frameTimeSum += delta;
        _frameSamples++;
        _slowestFrameMs = Math.Max(_slowestFrameMs, (float)(delta * 1000));
        _routeDistance = (_routeDistance + Math.Max(0, delta) * 8.5) % _route.LengthMeters;
        var pose = _route.GetPoseAtDistance(_routeDistance);
        var godotForward = ProceduralRoad.ToGodot(pose.Forward);
        var cadences = new[] { LeftCadence(_elapsed), 85, 115 };
        var lanes = new[]
        {
            -2.0f + (float)(0.7 * Math.Sin(_elapsed * 0.75)),
            0.0f,
            2.0f + (float)(0.7 * Math.Sin(_elapsed * 0.75 + Math.PI))
        };
        for (var i = 0; i < _riders.Length; i++)
        {
            var position = RiderLaneMath.OffsetPosition(pose.Position, pose.Forward, lanes[i]);
            _riders[i].UpdateFromRideState(ProceduralRoad.ToGodot(position), godotForward,
                31, cadences[i], 0, false, delta);
        }

        _buildings.UpdateForRideDistance(_routeDistance);
        _terrain.UpdateForRideDistance(_routeDistance);
        _vegetation.UpdateForRideDistance(_routeDistance);
        var viewIndex = (int)(_elapsed / 6.0) % _views.Length;
        _camera.SetView(_views[viewIndex]);
        _camera.Follow(ProceduralRoad.ToGodot(pose.Position), godotForward, delta);
        if (_captureRequested)
        {
            if (!_chaseCaptured && _elapsed >= 5.5) _chaseCaptured = CaptureFrame("RiderBikePreview-Chase.png");
            if (!_sideCaptured && _elapsed >= 11.5) _sideCaptured = CaptureFrame("RiderBikePreview-Side.png");
            if (!_frontCaptured && _elapsed >= 17.5) _frontCaptured = CaptureFrame("RiderBikePreview-FrontThreeQuarter.png");
        }
        if (_statsElapsed >= 5)
        {
            GD.Print($"[RiderBikePreview] fps={_frameSamples / _frameTimeSum:F1} " +
                $"slowestFrameMs={_slowestFrameMs:F1} riders={_riders.Length} " +
                $"trianglesPerRider={_riders[0].ApproximateTriangleCount}");
            _statsElapsed = _frameTimeSum = 0;
            _frameSamples = 0;
            _slowestFrameMs = 0;
        }
    }

    private bool CaptureFrame(string fileName)
    {
        var directory = ProjectSettings.GlobalizePath("res://Tests/previews");
        DirAccess.MakeDirRecursiveAbsolute(directory);
        var path = System.IO.Path.Combine(directory, fileName);
        var error = GetViewport().GetTexture().GetImage().SavePng(path);
        if (error != Error.Ok)
        {
            GD.PushWarning($"[RiderBikePreview] Could not save {fileName}: {error}");
            return false;
        }
        GD.Print($"[RiderBikePreview] Captured {path}");
        return true;
    }

    private static int LeftCadence(double seconds)
    {
        var phase = seconds % 18;
        if (phase < 2.5 || phase is >= 9 and < 11.5) return 0;
        return 55;
    }

    private void BuildWorld()
    {
        _definition = RouteCatalog.GetById("dunedin-rolling-loop")
            ?? throw new InvalidOperationException("Rolling City route package is unavailable.");
        _route = new VisualRoute(_definition);
        AddChild(ProceduralRoad.BuildRibbon(_route, ProceduralRoad.WidthMeters, 0, WodraWorldStyle.Asphalt));
        var assembly = typeof(RouteCatalog).Assembly;
        var prefix = $"Wodra.GameData.Routes.{_definition.Id}.";
        var buildings = RouteBuildingDataLoader.LoadEmbedded(assembly, prefix + "buildings.json", _definition.Id);
        _buildings = new BuildingLayer { Name = "PreviewBuildings" };
        AddChild(_buildings);
        _buildings.Configure(_route, _definition, buildings);

        var routePoints = RouteBackgroundStreetDataLoader.TryLoadEmbedded(assembly,
            prefix + "streets.json", _definition.Id)
            ?? throw new InvalidOperationException("Rolling City street data is missing.");
        var index = new BackgroundStreetGeometry.RouteSegmentSpatialIndex(_route.Samples);
        var corridors = BackgroundStreetGeometry.BuildTerrainCorridors(_route, routePoints, index);
        _terrain = new TerrainLayer { Name = "PreviewTerrain" };
        AddChild(_terrain);
        if (!_terrain.Configure(_route, prefix + "terrain.json", _definition.Id, corridors))
            throw new InvalidOperationException("Rolling City terrain package is missing.");
        var vegetationData = RouteVegetationDataLoader.LoadEmbedded(assembly,
            prefix + "vegetation.json", _definition.Id);
        _vegetation = new VegetationLayer { Name = "PreviewVegetation" };
        AddChild(_vegetation);
        _vegetation.Configure(_route, _definition, vegetationData, corridors);

        var sky = new Sky
        {
            SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = WodraWorldStyle.SkyTop,
                SkyHorizonColor = WodraWorldStyle.SkyHorizon,
                GroundBottomColor = WodraWorldStyle.GroundHorizon.Darkened(0.22f),
                GroundHorizonColor = WodraWorldStyle.GroundHorizon
            }
        };
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = sky,
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = WodraWorldStyle.AmbientEnergy,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
                FogEnabled = true,
                FogLightColor = WodraWorldStyle.GroundHorizon,
                FogDensity = WodraWorldStyle.FogDensity,
                FogAerialPerspective = WodraWorldStyle.FogAerialPerspective,
                FogSkyAffect = WodraWorldStyle.FogSkyAffect
            }
        });
        AddChild(new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-24, -34, 0),
            LightColor = WodraWorldStyle.Sunlight,
            LightEnergy = WodraWorldStyle.SunEnergy,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,
            DirectionalShadowMaxDistance = WodraWorldStyle.ShadowDistanceMeters,
            ShadowOpacity = 0.42f
        });
        _buildings.UpdateForRideDistance(_routeDistance);
        _terrain.UpdateForRideDistance(_routeDistance);
        _vegetation.UpdateForRideDistance(_routeDistance);
    }

    private static CanvasLayer CreateHud()
    {
        var layer = new CanvasLayer { Name = "PreviewReadout" };
        var panel = new PanelContainer
        {
            OffsetLeft = 24,
            OffsetTop = 24,
            OffsetRight = 490,
            OffsetBottom = 110
        };
        var label = new Label
        {
            Text = "ROAD BIKE / RIDER PREVIEW\nLeft coasts and restarts · Three jersey/frame variants\nCamera: chase → side → front three-quarter",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        panel.AddChild(label);
        layer.AddChild(panel);
        return layer;
    }
}
