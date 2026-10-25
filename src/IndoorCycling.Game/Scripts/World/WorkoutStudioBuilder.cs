using Godot;
using System.Linq;

/// <summary>Compact neutral indoor environment owned only by TrainingStudio activities.</summary>
public static class WorkoutStudioBuilder
{
    public static Node3D Build(VisualRoute route)
    {
        var root = new Node3D { Name = "WorkoutStudioEnvironment" };
        var samples = route.Samples;
        var minX = samples.Min(s => s.Position.X) - 28; var maxX = samples.Max(s => s.Position.X) + 28;
        var minZ = samples.Min(s => s.Position.Z) - 28; var maxZ = samples.Max(s => s.Position.Z) + 28;
        root.AddChild(new MeshInstance3D { Name = "NeutralStudioFloor", Position = new Vector3((minX + maxX) / 2, -3, (minZ + maxZ) / 2),
            Mesh = new PlaneMesh { Size = new Vector2(maxX - minX, maxZ - minZ) },
            MaterialOverride = Material(new Color(.12f, .15f, .17f)) });
        root.AddChild(Ribbon(route, -8, 8, new Color(.17f, .22f, .24f), 0.015f));
        root.AddChild(Ribbon(route, -7.7f, -7.55f, new Color(.88f, .54f, .23f), .035f));
        root.AddChild(Ribbon(route, 7.55f, 7.7f, new Color(.88f, .54f, .23f), .035f));
        root.AddChild(FinishLine(route));
        // Low, repeated perimeter lights communicate a training room without introducing scenery on the riding line.
        foreach (var point in samples.Where((_, index) => index % 8 == 0))
        {
            root.AddChild(new MeshInstance3D { Name = "StudioLight", Position = new Vector3(point.Position.X * 1.35f, 7, point.Position.Z * 1.35f),
                Mesh = new SphereMesh { Radius = .12f, Height = .24f }, MaterialOverride = Material(new Color(.95f, .75f, .48f), true) });
        }
        return root;
    }

    private static MeshInstance3D Ribbon(VisualRoute route, float left, float right, Color colour, float lift)
    {
        var tool = new SurfaceTool(); tool.Begin(Mesh.PrimitiveType.Triangles);
        for (var i = 0; i < route.Samples.Count - 1; i++)
        {
            var a = route.Samples[i]; var b = route.Samples[i + 1];
            var pa = Offset(a.Position, a.Forward, left, lift); var qa = Offset(a.Position, a.Forward, right, lift);
            var pb = Offset(b.Position, b.Forward, left, lift); var qb = Offset(b.Position, b.Forward, right, lift);
            tool.AddVertex(pa); tool.AddVertex(qa); tool.AddVertex(pb); tool.AddVertex(qa); tool.AddVertex(qb); tool.AddVertex(pb);
        }
        tool.GenerateNormals(); return new MeshInstance3D { Mesh = tool.Commit(), MaterialOverride = Material(colour) };
    }

    private static Vector3 Offset(System.Numerics.Vector3 p, System.Numerics.Vector3 forward, float lateral, float lift)
    { var side = new System.Numerics.Vector3(-forward.Z, 0, forward.X); return new Vector3(p.X + side.X * lateral, p.Y + lift, p.Z + side.Z * lateral); }
    private static MeshInstance3D FinishLine(VisualRoute route)
    {
        var p = route.Samples[0]; var side = new System.Numerics.Vector3(-p.Forward.Z, 0, p.Forward.X);
        var x = new Vector3(side.X * 8, .07f, side.Z * 8);
        return new MeshInstance3D { Name = "StudioStartFinish", Position = new Vector3(p.Position.X, p.Position.Y, p.Position.Z),
            Mesh = new BoxMesh { Size = new Vector3(0.18f, .04f, 16) }, MaterialOverride = Material(new Color(.91f, .85f, .68f)) };
    }
    private static StandardMaterial3D Material(Color colour, bool emission = false) => new() { AlbedoColor = colour,
        EmissionEnabled = emission, Emission = emission ? colour : Colors.Black, Roughness = .88f };
}
