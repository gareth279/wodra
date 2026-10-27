using Godot;
using System;
using System.Linq;
using NVector3 = System.Numerics.Vector3;

/// <summary>Track-only geometry. It consumes the shared reference route and has no ride-state authority.</summary>
public static class VelodromeTrackBuilder
{
	public const float RacingHalfWidthMeters = 3.5f;
	public const float SafetyApronInnerEdgeMeters = -7.5f;
	public const float InfieldFloorY = -7.6f;
    public static Node3D Build(VisualRoute route)
    {
        var root = new Node3D { Name = "VelodromeEnvironment" };
		// The centreline is the 250 m reference line; the 7 m racing surface is
		// centred on it. The blue apron is a separate inside surface.
		root.AddChild(BuildRibbon(route, -RacingHalfWidthMeters, RacingHalfWidthMeters, new Color(.38f, .22f, .15f), .015f));
		root.AddChild(BuildRibbon(route, SafetyApronInnerEdgeMeters, -RacingHalfWidthMeters, new Color(.08f, .31f, .58f), .025f));
		// Track markings measured from the inner edge (negative lane offsets).
		root.AddChild(BuildRibbon(route, -3.53f, -3.47f, new Color(.94f, .93f, .86f), .04f)); // inner boundary
		root.AddChild(BuildRibbon(route, -3.34f, -3.30f, new Color(.08f, .08f, .08f), .045f)); // 250 m measurement line
		root.AddChild(BuildRibbon(route, -2.62f, -2.54f, new Color(.88f, .19f, .16f), .05f)); // sprinter line
		root.AddChild(BuildRibbon(route, -1.02f, -.94f, new Color(.12f, .39f, .67f), .05f)); // stayer line
		root.AddChild(BuildRibbon(route, RacingHalfWidthMeters - .08f, RacingHalfWidthMeters, new Color(.83f, .82f, .74f), .05f));
        root.AddChild(BuildFinishLine(route));

        var bounds = route.Samples;
        var minX = bounds.Min(p => p.Position.X) - 16;
        var maxX = bounds.Max(p => p.Position.X) + 16;
        var minZ = bounds.Min(p => p.Position.Z) - 16;
        var maxZ = bounds.Max(p => p.Position.Z) + 16;
        var floor = new MeshInstance3D
        {
            Name = "IndoorInfieldAndFloor",
			Position = new Vector3((minX + maxX) / 2, InfieldFloorY - .5f, (minZ + maxZ) / 2),
            Mesh = new PlaneMesh { Size = new Vector2(maxX - minX, maxZ - minZ) },
            MaterialOverride = Material(new Color(.31f, .38f, .34f))
        };
        root.AddChild(floor);
        root.AddChild(new MeshInstance3D
        {
			Name = "FlatCentralInfield",
			Position = new Vector3(0, InfieldFloorY, 0),
			Mesh = new PlaneMesh { Size = new Vector2(34, 40) },
            MaterialOverride = Material(new Color(.16f, .32f, .25f))
        });
        root.AddChild(new MeshInstance3D
        {
            Name = "IndoorRoof",
            Position = new Vector3(0, 28, 0),
            Mesh = new PlaneMesh { Size = new Vector2(180, 160) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(.18f, .21f, .23f), Emission = new Color(.08f, .09f, .10f),
                Roughness = .94f, CullMode = BaseMaterial3D.CullModeEnum.Disabled
            }
        });
        for (var z = -36; z <= 36; z += 18)
        {
            root.AddChild(new MeshInstance3D
            {
                Name = "RoofLight",
                Position = new Vector3(0, 27.75f, z),
                Mesh = new BoxMesh { Size = new Vector3(62, .12f, .22f) },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(.92f, .89f, .77f), Emission = new Color(.9f, .77f, .5f),
                    Roughness = .5f
                }
            });
        }
        // Perimeter supports establish an indoor arena scale without placing
        // columns in the cyclist's sightline along either straight.
        foreach (var x in new[] { -68f, 68f })
        foreach (var z in new[] { -52f, 52f })
        {
            root.AddChild(new MeshInstance3D
            {
                Name = "ArenaSupport",
                Position = new Vector3(x, 10, z),
                Mesh = new BoxMesh { Size = new Vector3(.55f, 20, .55f) },
                MaterialOverride = Material(new Color(.25f, .28f, .29f))
            });
        }
        foreach (var x in new[] { -80f, 80f })
            root.AddChild(BuildArenaWall(new Vector3(x, 14, 0), new Vector3(.8f, 28, 150)));
        foreach (var z in new[] { -75f, 75f })
            root.AddChild(BuildArenaWall(new Vector3(0, 14, z), new Vector3(160, 28, .8f)));
        return root;
    }

    private static MeshInstance3D BuildRibbon(VisualRoute route, float left, float right, Color colour, float lift)
    {
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (var i = 0; i < route.Samples.Count - 1; i++)
        {
            var a = route.Samples[i]; var b = route.Samples[i + 1];
            var pa = Point(a.Position, a.Forward, a.DistanceMeters, left, route, lift);
            var pb = Point(b.Position, b.Forward, b.DistanceMeters, left, route, lift);
            var qa = Point(a.Position, a.Forward, a.DistanceMeters, right, route, lift);
            var qb = Point(b.Position, b.Forward, b.DistanceMeters, right, route, lift);
            var forward = ToGodot(b.Position - a.Position).Normalized();
            var lateral = (qa - pa).Normalized();
            var normal = forward.Cross(lateral).Normalized();
            AddQuad(surface, pa, pb, qa, qb, normal);
        }
        var ribbon = new MeshInstance3D
        {
            Name = "BankedTrackSurface", Mesh = surface.Commit(), MaterialOverride = Material(colour)
        };
        ribbon.AddToGroup("VelodromeTrackSurfaces");
        return ribbon;
    }

    private static MeshInstance3D BuildFinishLine(VisualRoute route)
    {
        // A broad transverse white band at the authored 0 m start/finish chainage.
        var surface = new SurfaceTool(); surface.Begin(Mesh.PrimitiveType.Triangles);
        var pose = route.GetPoseAtDistance(0);
        var left = ToGodot(RiderLaneMath.LocalLeft(pose.Forward));
        var center = ToGodot(pose.Position);
		Vector3 At(float lateral) => center + left * lateral + Vector3.Up * (lateral * pose.BankSlope + .06f);
		var forward = ToGodot(pose.Forward) * .12f;
		var inner = At(SafetyApronInnerEdgeMeters);
		var outer = At(RacingHalfWidthMeters);
		var normal = ToGodot(pose.Forward).Cross(left).Normalized();
		AddQuad(surface, inner, inner + forward, outer, outer + forward, normal);
        return new MeshInstance3D { Name = "StartFinishLine", Mesh = surface.Commit(), MaterialOverride = Material(new Color(.95f,.94f,.86f)) };
    }

    private static MeshInstance3D BuildArenaWall(Vector3 position, Vector3 size) => new()
    {
        Name = "IndoorArenaWall",
        Position = position,
        Mesh = new BoxMesh { Size = size },
        MaterialOverride = Material(new Color(.32f, .35f, .36f))
    };

    private static Vector3 Point(NVector3 position, NVector3 forward, double distance, float lateral,
        VisualRoute route, float lift)
    {
        var left = RiderLaneMath.LocalLeft(forward);
        var slope = route.GetPoseAtDistance(distance).BankSlope;
        var result = position + left * lateral + NVector3.UnitY * (lateral * slope + lift);
        return ToGodot(result);
    }

    private static void AddQuad(SurfaceTool surface, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
    {
        surface.SetNormal(normal); surface.AddVertex(a); surface.SetNormal(normal); surface.AddVertex(b); surface.SetNormal(normal); surface.AddVertex(c);
        surface.SetNormal(normal); surface.AddVertex(c); surface.SetNormal(normal); surface.AddVertex(b); surface.SetNormal(normal); surface.AddVertex(d);
    }

    private static StandardMaterial3D Material(Color colour) => new()
    { AlbedoColor = colour, Roughness = .86f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
    private static Vector3 ToGodot(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);
}
