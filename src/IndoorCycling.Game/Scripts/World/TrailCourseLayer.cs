using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IndoorCycling.Core.Simulation;
using NVector3 = System.Numerics.Vector3;

/// <summary>Route-content-driven, visual-only line geometry and challenge observation for narrow XC courses.</summary>
public partial class TrailCourseLayer : Node3D
{
    public const float TrailWidthMeters = TrailEnvironmentBuilder.MountainBikeRibbonWidthMeters;
    private VisualRoute? _route;
    private TrailFeatureChallenge? _challenge;
    private RouteDefinition? _definition;
    public int JumpLineCount => _definition?.TrailFeatures.Count ?? 0;
    public TrailFeatureFrame LastFrame { get; private set; } = new();
    public int TotalScore => _challenge?.TotalScore ?? 0;
    public int CompletedLines => _challenge?.CompletedLines ?? 0;
    public double LateralCorridorHalfWidth(double distanceMeters) =>
        Math.Min(_definition?.Capabilities.MaximumLateralOffsetMeters ?? 0,
            _challenge?.LateralCorridorHalfWidth(distanceMeters, TrailWidthMeters * .5) ?? 0);

    public void Configure(VisualRoute route, RouteDefinition definition)
    {
        if (!definition.Capabilities.SupportsTrailFeatures || definition.TrailFeatures.Count == 0)
            throw new ArgumentException("Trail feature geometry requires explicit route capability and authored features.", nameof(definition));
        _route = route;
        _definition = definition;
        _challenge = new TrailFeatureChallenge(definition.TrailFeatures, route.LengthMeters, definition.TrailRollers);
        foreach (var feature in definition.TrailFeatures)
        {
            AddChild(BuildFeatureRibbon(route, feature));
            AddChild(BuildFeatureConnectors(route, feature));
            AddChild(BuildFeatureSign(route, feature));
        }
        foreach (var berm in definition.TrailBerms) AddChild(BuildBerm(route, berm));
        foreach (var roller in definition.TrailRollers) AddChild(BuildRoller(route, roller));
    }

    public TrailFeatureFrame UpdateRide(double distanceMeters, double lateralMeters, double speedKph,
        bool paused, bool scoringEnabled)
    {
        LastFrame = _challenge?.Update(distanceMeters, lateralMeters, speedKph, paused, scoringEnabled) ?? new();
        return LastFrame;
    }

    public void SuppressNextSweep(double distanceMeters, double lateralMeters)
    {
        _challenge?.SuppressNextSweep(distanceMeters, lateralMeters);
        LastFrame = _challenge?.Update(distanceMeters, lateralMeters, 0, paused: true, scoringEnabled: false) ?? new();
    }

    private static MeshInstance3D BuildFeatureRibbon(VisualRoute route, TrailFeatureDefinition feature)
    {
        var centers = feature.Path.Select(point => ToNumerics(FeaturePosition(route, point))).ToArray();
        return BuildRibbon($"JumpLine_{feature.Id}", centers, TrailWidthMeters,
            new Color(.39f, .27f, .16f));
    }

    private static MeshInstance3D BuildFeatureConnectors(VisualRoute route, TrailFeatureDefinition feature)
    {
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        AddWindow(feature.StartMeters, feature.StartMeters + Math.Abs(feature.RequiredLateralMeters) * 14.34);
        AddWindow(feature.EndMeters - Math.Abs(feature.RequiredLateralMeters) * 14.34, feature.EndMeters);
        var mesh = surface.Commit();
        return new MeshInstance3D
        {
            Name = $"FeatureMerges_{feature.Id}", Mesh = mesh,
            MaterialOverride = Material(new Color(.31f, .25f, .17f))
        };

        void AddWindow(double start, double end)
        {
            var points = feature.Path.Where(point => point.RouteDistanceMeters >= start && point.RouteDistanceMeters <= end).ToArray();
            if (points.Length < 2) return;
            for (var index = 0; index < points.Length - 1; index++)
            {
                var a = points[index]; var b = points[index + 1];
                var centerA = ToGodot(route.GetPoseAtDistance(a.RouteDistanceMeters).Position);
                var centerB = ToGodot(route.GetPoseAtDistance(b.RouteDistanceMeters).Position);
                var lineA = FeaturePosition(route, a);
                var lineB = FeaturePosition(route, b);
                AddTriangle(centerA, lineA, centerB);
                AddTriangle(lineA, lineB, centerB);
            }
        }

        void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = (b - a).Cross(c - a).Normalized();
            if (normal.LengthSquared() < 1e-5f) normal = Vector3.Up;
            if (normal.Y < 0) normal = -normal;
            surface.SetNormal(normal); surface.AddVertex(a);
            surface.SetNormal(normal); surface.AddVertex(b);
            surface.SetNormal(normal); surface.AddVertex(c);
        }
    }

    private static Vector3 FeaturePosition(VisualRoute route, TrailFeaturePathPoint point)
    {
        var pose = route.GetPoseAtDistance(point.RouteDistanceMeters);
        var side = RiderLaneMath.LocalLeft(pose.Forward);
        var position = pose.Position + side * (float)point.LateralMeters + NVector3.UnitY * (float)point.SurfaceLiftMeters;
        return new Vector3(position.X, position.Y, position.Z);
    }

    private static MeshInstance3D BuildBerm(VisualRoute route, TrailBermDefinition berm)
    {
        var surface = new SurfaceTool(); surface.Begin(Mesh.PrimitiveType.Triangles);
        var sampleCount = Math.Max(2, (int)Math.Ceiling((berm.EndMeters - berm.StartMeters) / 2));
        var previous = GetCrossSection(0);
        for (var index = 1; index <= sampleCount; index++)
        {
            var current = GetCrossSection(index);
            AddQuad(previous.Inner, current.Inner, previous.Outer, current.Outer);
            previous = current;
        }
        return new MeshInstance3D { Name = $"TrailBerm_{berm.Id}", Mesh = surface.Commit(),
            MaterialOverride = Material(new Color(.25f, .21f, .15f)) };

        (Vector3 Inner, Vector3 Outer) GetCrossSection(int index)
        {
            var t = index / (double)sampleCount;
            var distance = berm.StartMeters + (berm.EndMeters - berm.StartMeters) * t;
            var pose = route.GetPoseAtDistance(distance);
            var center = ToGodot(pose.Position);
            var side = RiderLaneMath.LocalLeft(pose.Forward);
            var crown = (float)Math.Sin(Math.PI * t) * berm.HeightMeters;
            var sideGodot = ToGodot(side);
            var inner = center + sideGodot * (berm.Side * TrailWidthMeters * .5f) + Vector3.Up * .04f;
            var outer = center + sideGodot * (berm.Side * (TrailWidthMeters * .5f + berm.WidthMeters))
                + Vector3.Up * (crown + .04f);
            return (inner, outer);
        }

        void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Add(a, b, c); Add(c, b, d);
        }
        void Add(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = (b - a).Cross(c - a).Normalized();
            if (normal.LengthSquared() < 1e-5f) return;
            if (normal.Y < 0) normal = -normal;
            surface.SetNormal(normal); surface.AddVertex(a);
            surface.SetNormal(normal); surface.AddVertex(b);
            surface.SetNormal(normal); surface.AddVertex(c);
        }
    }

    private static MeshInstance3D BuildRoller(VisualRoute route, TrailRollerDefinition roller)
    {
        var points = new List<NVector3>();
        for (var distance = roller.StartMeters; distance < roller.EndMeters; distance += 1)
            points.Add(RollerPoint(distance));
        points.Add(RollerPoint(roller.EndMeters));
        return BuildRibbon($"TrailRoller_{roller.Id}", points, TrailWidthMeters * .92f,
            new Color(.41f, .30f, .19f));

        NVector3 RollerPoint(double distance)
        {
            var t = (distance - roller.StartMeters) / (roller.EndMeters - roller.StartMeters);
            var normalizedPeak = (roller.PeakMeters - roller.StartMeters) / (roller.EndMeters - roller.StartMeters);
            var height = t <= normalizedPeak
                ? roller.HeightMeters * Math.Sin(Math.PI * .5 * t / normalizedPeak)
                : roller.HeightMeters * Math.Cos(Math.PI * .5 * (t - normalizedPeak) / (1 - normalizedPeak));
            return route.GetPoseAtDistance(distance).Position + NVector3.UnitY * (float)height;
        }
    }

    private static MeshInstance3D BuildRibbon(string name, IReadOnlyList<NVector3> centers, float width, Color color)
    {
        var surface = new SurfaceTool(); surface.Begin(Mesh.PrimitiveType.Triangles);
        for (var index = 0; index < centers.Count - 1; index++)
        {
            var start = Section(index); var end = Section(index + 1);
            Add(start.Left, end.Left, start.Right);
            Add(start.Right, end.Left, end.Right);
        }
        return new MeshInstance3D { Name = name, Mesh = surface.Commit(), MaterialOverride = Material(color) };

        (Vector3 Left, Vector3 Right) Section(int index)
        {
            var direction = index == 0 ? centers[1] - centers[0]
                : index == centers.Count - 1 ? centers[^1] - centers[^2]
                : centers[index + 1] - centers[index - 1];
            direction.Y = 0;
            if (direction.LengthSquared() < 1e-6f) direction = -NVector3.UnitZ;
            var left = NVector3.Normalize(NVector3.Cross(NVector3.UnitY, NVector3.Normalize(direction)));
            return (ToGodot(centers[index] + left * (width * .5f)), ToGodot(centers[index] - left * (width * .5f)));
        }
        void Add(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = (b - a).Cross(c - a).Normalized();
            if (normal.LengthSquared() < 1e-5f) return;
            if (normal.Y < 0) normal = -normal;
            surface.SetNormal(normal); surface.AddVertex(a);
            surface.SetNormal(normal); surface.AddVertex(b);
            surface.SetNormal(normal); surface.AddVertex(c);
        }
    }

    private static Node3D BuildFeatureSign(VisualRoute route, TrailFeatureDefinition feature)
    {
        var root = new Node3D { Name = $"TrailFeatureSign_{feature.Id}" };
        var pose = route.GetPoseAtDistance(Math.Max(0, feature.StartMeters - 36));
        var forward = ToGodot(pose.Forward).Normalized();
        var side = ToGodot(RiderLaneMath.LocalLeft(pose.Forward));
        // Keep the sign on the camera-left shoulder so the persistent route/elevation inset does not cover it.
        root.Position = ToGodot(pose.Position) + side * 3.5f;
        root.Basis = Basis.LookingAt(forward, Vector3.Up);
        var postMaterial = Material(new Color(.24f, .20f, .14f));
        root.AddChild(new MeshInstance3D { Name = "Post", Mesh = new BoxMesh { Size = new Vector3(.09f, 1.45f, .09f) },
            Position = new Vector3(0, .72f, 0), MaterialOverride = postMaterial });
        root.AddChild(new MeshInstance3D { Name = "Board", Mesh = new BoxMesh { Size = new Vector3(1.6f, .48f, .08f) },
            Position = new Vector3(0, 1.25f, 0), MaterialOverride = Material(new Color(.72f, .50f, .22f)) });
        root.AddChild(new Label3D { Name = "LineChoice", Text = "JUMP  |  ROLL", Position = new Vector3(0, 1.25f, -.055f),
            PixelSize = .0035f, FontSize = 22, OutlineSize = 4, Modulate = new Color(.1f, .08f, .05f),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            NoDepthTest = true });
        return root;
    }

    private static StandardMaterial3D Material(Color colour) => new()
    { AlbedoColor = colour, Roughness = 1, CullMode = BaseMaterial3D.CullModeEnum.Disabled };

    private static Vector3 ToGodot(NVector3 value) => new(value.X, value.Y, value.Z);
    private static NVector3 ToNumerics(Vector3 value) => new(value.X, value.Y, value.Z);
}
