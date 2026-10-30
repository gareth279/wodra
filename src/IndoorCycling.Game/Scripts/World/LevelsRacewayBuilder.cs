using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using NVector3 = System.Numerics.Vector3;

/// <summary>Lightweight outdoor presentation of the mapped Levels Raceway workout centreline.</summary>
public static class LevelsRacewayBuilder
{
    private const float RoadHalfWidth = 6.5f;
    private const float ShoulderHalfWidth = 10.5f;
    private const float PanelLift = 0.035f;

    private readonly record struct Corner(double Distance, int Sign, float Strength);

    public static Node3D Build(VisualRoute route)
    {
        var root = new Node3D { Name = "LevelsRacewayEnvironment" };
        var center = route.BoundsCenter;
        var minX = route.Samples.Min(sample => sample.Position.X) - 180;
        var maxX = route.Samples.Max(sample => sample.Position.X) + 180;
        var minZ = route.Samples.Min(sample => sample.Position.Z) - 180;
        var maxZ = route.Samples.Max(sample => sample.Position.Z) + 180;

        root.AddChild(new MeshInstance3D
        {
            Name = "SouthCanterburyGrassField",
            Position = new Vector3((minX + maxX) / 2, -0.28f, (minZ + maxZ) / 2),
            Mesh = new PlaneMesh { Size = new Vector2(maxX - minX, maxZ - minZ), SubdivideWidth = 8, SubdivideDepth = 8 },
            MaterialOverride = Material(new Color(.36f, .43f, .25f), roughness: 1f)
        });
        root.AddChild(ProceduralRoad.BuildRibbon(route, ShoulderHalfWidth * 2, 0, new Color(.39f, .45f, .27f)));
        root.AddChild(ProceduralRoad.BuildRibbon(route, RoadHalfWidth * 2, .025f, WodraWorldStyle.Asphalt));
        root.AddChild(ProceduralRoad.BuildRibbon(route, .20f, .067f, new Color(.91f, .91f, .83f), -6.18f));
        root.AddChild(ProceduralRoad.BuildRibbon(route, .20f, .067f, new Color(.91f, .91f, .83f), 6.18f));

        var corners = FindCorners(route);
        root.AddChild(BuildKerbs(route, corners));
        root.AddChild(BuildStartFinish(route));
        root.AddChild(BuildSafetyBarriers(route, corners));
        root.AddChild(BuildPaddock(route));
        root.AddChild(BuildTrees(route));
        return root;
    }

    private static IReadOnlyList<Corner> FindCorners(VisualRoute route)
    {
        var samples = route.Samples;
        const int lookAhead = 4;
        var candidates = new List<Corner>();
        for (var i = lookAhead; i < samples.Count - lookAhead; i += 2)
        {
            var incoming = Horizontal(samples[i].Position - samples[i - lookAhead].Position);
            var outgoing = Horizontal(samples[i + lookAhead].Position - samples[i].Position);
            if (incoming.LengthSquared() < 1e-5f || outgoing.LengthSquared() < 1e-5f) continue;
            incoming = NVector3.Normalize(incoming); outgoing = NVector3.Normalize(outgoing);
            var cross = incoming.X * outgoing.Z - incoming.Z * outgoing.X;
            var dot = Math.Clamp(NVector3.Dot(incoming, outgoing), -1f, 1f);
            var strength = MathF.Acos(dot);
            if (strength < .22f || MathF.Abs(cross) < .1f) continue;
            candidates.Add(new Corner(samples[i].DistanceMeters, Math.Sign(cross), strength));
        }

        // Keep distinct, strongest bend centres, then restore route order.
        var selected = new List<Corner>();
        foreach (var candidate in candidates.OrderByDescending(item => item.Strength))
        {
            if (selected.Any(existing => CircularDistance(existing.Distance, candidate.Distance, route.LengthMeters) < 100))
                continue;
            selected.Add(candidate);
            if (selected.Count == 10) break;
        }
        return selected.OrderBy(item => item.Distance).ToArray();
    }

    private static Node3D BuildKerbs(VisualRoute route, IReadOnlyList<Corner> corners)
    {
        var group = new Node3D { Name = "PaintedInsideKerbs" };
        var red = new SurfaceTool(); red.Begin(Mesh.PrimitiveType.Triangles);
        var pale = new SurfaceTool(); pale.Begin(Mesh.PrimitiveType.Triangles);
        var anyRed = false; var anyPale = false;
        for (var i = 0; i < route.Samples.Count - 1; i++)
        {
            var sample = route.Samples[i];
            var corner = corners.FirstOrDefault(item => CircularDistance(item.Distance, sample.DistanceMeters, route.LengthMeters) <= 25);
            if (corner.Strength <= 0) continue;
            var mesh = ((int)(sample.DistanceMeters / 4) % 2 == 0) ? red : pale;
            if (ReferenceEquals(mesh, red)) anyRed = true; else anyPale = true;
            AddRibbonSegment(mesh, route.Samples[i], route.Samples[i + 1], corner.Sign * (RoadHalfWidth - .25f), .52f, .085f);
        }
        if (anyRed) group.AddChild(Surface(red, "RedKerbBlocks", new Color(.76f, .18f, .13f)));
        if (anyPale) group.AddChild(Surface(pale, "WhiteKerbBlocks", new Color(.91f, .88f, .77f)));
        return group;
    }

    private static Node3D BuildSafetyBarriers(VisualRoute route, IReadOnlyList<Corner> corners)
    {
        var root = new Node3D { Name = "TracksideSafetyBarriers" };
        var placements = new List<Transform3D>();
        for (var i = 0; i < route.Samples.Count - 1; i++)
        {
            var sample = route.Samples[i];
            var corner = corners.FirstOrDefault(item => CircularDistance(item.Distance, sample.DistanceMeters, route.LengthMeters) <= 48);
            if (corner.Strength <= 0 || i % 2 != 0) continue;
            var forward = ToGodot(sample.Forward).Normalized();
            var outside = -corner.Sign;
            var side = new Vector3(-forward.Z, 0, forward.X) * outside;
            var position = ToGodot(sample.Position) + side * 11.8f + Vector3.Up * .58f;
            placements.Add(new Transform3D(Basis.LookingAt(forward, Vector3.Up), position));
        }
        if (placements.Count == 0) return root;
        var mesh = new BoxMesh { Size = new Vector3(.32f, 1.05f, 2.45f) };
        var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = placements.Count };
        for (var i = 0; i < placements.Count; i++) multi.SetInstanceTransform(i, placements[i]);
        root.AddChild(new MultiMeshInstance3D
        {
            Name = "LowWhiteSafetyBarriers", Multimesh = multi,
            MaterialOverride = Material(new Color(.73f, .75f, .71f), roughness: .85f)
        });
        return root;
    }

    private static Node3D BuildPaddock(VisualRoute route)
    {
        var root = new Node3D { Name = "PaddockAndPitBuildings" };
        // Modest, unbranded club garages alongside the start/finish straight.
        var pose = route.GetPoseAtDistance(1830);
        var forward = ToGodot(pose.Forward).Normalized();
        var side = new Vector3(-forward.Z, 0, forward.X);
        var basis = Basis.LookingAt(forward, Vector3.Up);
        var center = ToGodot(pose.Position) + side * 27;
        AddBox(root, "ClubPitGarageBlock", center + Vector3.Up * 3.2f, new Vector3(34, 6.4f, 11), new Color(.67f, .65f, .56f), basis);
        AddBox(root, "GarageRoof", center + Vector3.Up * 6.55f, new Vector3(36, .35f, 12.4f), new Color(.22f, .25f, .27f), basis);
        for (var bay = -3; bay <= 3; bay++)
        {
            var door = center + side * (bay * 4.15f) + Vector3.Up * 1.65f + forward * 5.55f;
            AddBox(root, $"GarageBay{bay + 4}", door, new Vector3(3.25f, 3.25f, .12f), new Color(.32f, .35f, .34f), basis);
        }

        // A small marshal shelter and generic paddock containers add scale without crowding the view.
        var marshalPose = route.GetPoseAtDistance(620);
        AddBox(root, "MarshalPost", ToGodot(marshalPose.Position) + new Vector3(0, 1.4f, 0) + side * -19,
            new Vector3(4.5f, 2.8f, 4.5f), new Color(.73f, .66f, .49f), basis);
        var containerPose = route.GetPoseAtDistance(1900);
        var containerForward = ToGodot(containerPose.Forward).Normalized();
        var containerSide = new Vector3(-containerForward.Z, 0, containerForward.X);
        AddBox(root, "UnbrandedPaddockContainer", ToGodot(containerPose.Position) + containerSide * -33 + Vector3.Up * 1.25f,
            new Vector3(2.6f, 2.5f, 8.5f), new Color(.37f, .43f, .42f), Basis.LookingAt(containerForward, Vector3.Up));
        return root;
    }

    private static Node3D BuildTrees(VisualRoute route)
    {
        var root = new Node3D { Name = "SparsePaddockTrees" };
        var treeMesh = new SphereMesh { Radius = 1.8f, Height = 4.7f, RadialSegments = 7, Rings = 4 };
        var transforms = new List<Transform3D>();
        // A few small tree clusters at the distant property edge; the track remains open and visible.
        for (var d = 120.0; d < route.LengthMeters; d += 185)
        {
            var pose = route.GetPoseAtDistance(d);
            var forward = ToGodot(pose.Forward).Normalized();
            var side = new Vector3(-forward.Z, 0, forward.X);
            var sign = ((int)(d / 185) % 2 == 0) ? 1 : -1;
            var position = ToGodot(pose.Position) + side * (sign * (58 + (int)d % 17)) + Vector3.Up * 2.25f;
            var scale = new Vector3(1f + ((int)d % 3) * .16f, .8f + ((int)d % 4) * .09f, 1f);
            transforms.Add(new Transform3D(Basis.Identity.Scaled(scale), position));
        }
        var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = treeMesh, InstanceCount = transforms.Count };
        for (var i = 0; i < transforms.Count; i++) multi.SetInstanceTransform(i, transforms[i]);
        root.AddChild(new MultiMeshInstance3D
        {
            Name = "PaddockTreeCanopies", Multimesh = multi,
            MaterialOverride = Material(new Color(.25f, .37f, .22f), roughness: 1f)
        });
        return root;
    }

    private static MeshInstance3D BuildStartFinish(VisualRoute route)
    {
        var pose = route.GetPoseAtDistance(0);
        var center = ToGodot(pose.Position) + Vector3.Up * .085f;
        var forward = ToGodot(pose.Forward).Normalized();
        var side = new Vector3(-forward.Z, 0, forward.X);
        var mesh = new SurfaceTool(); mesh.Begin(Mesh.PrimitiveType.Triangles);
        var halfAcross = RoadHalfWidth - .25f; var halfAlong = .16f;
        var a = center - side * halfAcross - forward * halfAlong;
        var b = center - side * halfAcross + forward * halfAlong;
        var c = center + side * halfAcross - forward * halfAlong;
        var d = center + side * halfAcross + forward * halfAlong;
        AddQuad(mesh, a, b, c, d);
        return Surface(mesh, "StartFinishLine", new Color(.94f, .93f, .85f));
    }

    private static void AddRibbonSegment(SurfaceTool surface, VisualRouteSample start, VisualRouteSample end,
        float offset, float width, float lift)
    {
        var p = ToGodot(start.Position); var q = ToGodot(end.Position);
        var sideA = new Vector3(-start.Forward.Z, 0, start.Forward.X).Normalized();
        var sideB = new Vector3(-end.Forward.Z, 0, end.Forward.X).Normalized();
        var a = p + sideA * (offset - width / 2) + Vector3.Up * lift;
        var b = q + sideB * (offset - width / 2) + Vector3.Up * lift;
        var c = p + sideA * (offset + width / 2) + Vector3.Up * lift;
        var d = q + sideB * (offset + width / 2) + Vector3.Up * lift;
        AddQuad(surface, a, b, c, d);
    }

    private static void AddQuad(SurfaceTool surface, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        surface.SetNormal(Vector3.Up); surface.AddVertex(a); surface.SetNormal(Vector3.Up); surface.AddVertex(b);
        surface.SetNormal(Vector3.Up); surface.AddVertex(c); surface.SetNormal(Vector3.Up); surface.AddVertex(c);
        surface.SetNormal(Vector3.Up); surface.AddVertex(b); surface.SetNormal(Vector3.Up); surface.AddVertex(d);
    }

    private static MeshInstance3D Surface(SurfaceTool tool, string name, Color colour) => new()
    {
        Name = name, Mesh = tool.Commit(), MaterialOverride = Material(colour, roughness: .9f)
    };

    private static void AddBox(Node3D parent, string name, Vector3 position, Vector3 size, Color color, Basis? basis = null)
    {
        parent.AddChild(new MeshInstance3D
        {
            Name = name,
            Transform = new Transform3D(basis ?? Basis.Identity, position),
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = Material(color, roughness: .88f)
        });
    }

    private static double CircularDistance(double a, double b, double length)
    {
        var difference = Math.Abs(a - b) % length;
        return Math.Min(difference, length - difference);
    }

    private static NVector3 Horizontal(NVector3 value) => new(value.X, 0, value.Z);
    private static Vector3 ToGodot(NVector3 value) => new(value.X, value.Y, value.Z);

    private static StandardMaterial3D Material(Color colour, float roughness = .92f) => new()
    {
        AlbedoColor = colour, Roughness = roughness, CullMode = BaseMaterial3D.CullModeEnum.Disabled
    };
}
