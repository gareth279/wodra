using System;
using System.Linq;
using Godot;

/// <summary>Read-only view of the session: no speed integration or trainer access.</summary>
public partial class RideWorldView : Node3D
{
    [Export] public bool DebugPoseLogging { get; set; }
    private RideSessionNode _session = default!;
    private RouteDefinition? _loadedRoute;
    private VisualRoute? _geometry;
    private Node3D? _routeRoot;
    private Node3D _rider = default!;
    private ChaseCamera _camera = default!;
    private double _debugElapsed;
    private bool _wasActive;

    public override void _Ready()
    {
        _session = GetNode<RideSessionNode>("../RideSessionNode");
        _camera = new ChaseCamera { Name = "ChaseCamera" };
        AddChild(_camera);
        _rider = CreateRider();
        AddChild(_rider);
        AddChild(new DirectionalLight3D
        {
            Name = "Sun", RotationDegrees = new Vector3(-45, -30, 0), LightEnergy = 1.2f
        });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.55f, 0.73f, 0.9f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.8f, 0.85f, 1),
                AmbientLightEnergy = 0.6f
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
        var position = ProceduralRoad.ToGodot(pose.Position) + Vector3.Up * 0.04f;
        var forward = ProceduralRoad.ToGodot(pose.Forward);
        _rider.GlobalPosition = position;
        _rider.LookAt(position + forward, Vector3.Up);
        _camera.Follow(position, forward, delta);
        _debugElapsed += delta;
        if (DebugPoseLogging && _debugElapsed >= 1)
        {
            _debugElapsed = 0;
            GD.Print($"Ride view: total={distance:F1}m loop={pose.LoopDistanceMeters:F1}/{_geometry.LengthMeters:F1}m " +
                $"position={position} segment={_loadedRoute!.GetSampleAt(distance).SegmentLabel} " +
                $"grade={_loadedRoute.GetGradientAt(distance):F1}%");
        }
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
        _routeRoot = new Node3D { Name = "RouteGeometry" };
        AddChild(_routeRoot);
        _routeRoot.AddChild(ProceduralRoad.BuildRibbon(_geometry, 60, -0.08f, new Color(0.28f, 0.45f, 0.22f)));
        _routeRoot.AddChild(ProceduralRoad.BuildRibbon(_geometry, ProceduralRoad.WidthMeters, 0, new Color(0.17f, 0.18f, 0.2f)));
        _routeRoot.AddChild(ProceduralRoad.BuildRibbon(_geometry, 0.12f, 0.015f, new Color(0.9f, 0.82f, 0.35f)));
        foreach (float offset in new[] { -3.25f, 3.25f })
            _routeRoot.AddChild(ProceduralRoad.BuildRibbon(_geometry, 0.12f, 0.02f, Colors.White, offset));
        var positions = _geometry.Vertices.Select(v => v.Position).ToArray();
        float minX = positions.Min(p => p.X), maxX = positions.Max(p => p.X);
        float minZ = positions.Min(p => p.Z), maxZ = positions.Max(p => p.Z);
        _routeRoot.AddChild(new MeshInstance3D
        {
            Name = "Ground",
            Position = new Vector3((minX + maxX) / 2, positions.Min(p => p.Y) - 1, (minZ + maxZ) / 2),
            Mesh = new PlaneMesh { Size = new Vector2(maxX - minX + 400, maxZ - minZ + 400) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.36f, 0.18f), Roughness = 1 }
        });
        GameLogger.Log("visual_route_loaded", new
        {
            route.Id, route.DistanceMeters, vertices = _geometry.Vertices.Count,
            closureCorrectionMeters = _geometry.ClosureCorrection.ToString()
        });
    }

    private static Node3D CreateRider()
    {
        var rider = new Node3D { Name = "PlaceholderCyclist" };
        var dark = new StandardMaterial3D { AlbedoColor = new Color(0.06f, 0.06f, 0.08f), Roughness = 1 };
        var blue = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.45f, 0.95f) };
        foreach (float z in new[] { -0.65f, 0.65f })
            rider.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.34f, BottomRadius = 0.34f, Height = 0.09f },
                Position = new Vector3(0, 0.34f, z), Rotation = new Vector3(0, 0, Mathf.Pi / 2), MaterialOverride = dark
            });
        rider.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.12f, 0.15f, 1.3f) },
            Position = new Vector3(0, 0.62f, 0), MaterialOverride = blue
        });
        rider.AddChild(new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = 0.23f, Height = 0.85f },
            Position = new Vector3(0, 1.18f, 0), Rotation = new Vector3(-0.3f, 0, 0), MaterialOverride = blue
        });
        rider.AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.19f, Height = 0.38f },
            Position = new Vector3(0, 1.78f, -0.18f), MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.Orange }
        });
        rider.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.65f, 0.08f, 0.08f) },
            Position = new Vector3(0, 0.95f, -0.65f), MaterialOverride = dark
        });
        return rider;
    }
}
