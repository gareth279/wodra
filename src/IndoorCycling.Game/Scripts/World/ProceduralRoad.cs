using Godot;

public static class ProceduralRoad
{
    public const float WidthMeters = 7;

    public static MeshInstance3D BuildRibbon(VisualRoute route, float width, float height, Color colour, float offset = 0)
    {
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < route.Vertices.Count - 1; i++)
        {
            var a = Edge(route, i, width, height, offset);
            var b = Edge(route, i + 1, width, height, offset);
            Vertex(surface, a.Left, a.Normal);
            Vertex(surface, b.Left, b.Normal);
            Vertex(surface, a.Right, a.Normal);
            Vertex(surface, a.Right, a.Normal);
            Vertex(surface, b.Left, b.Normal);
            Vertex(surface, b.Right, b.Normal);
        }
        return new MeshInstance3D
        {
            Mesh = surface.Commit(),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = colour,
                Roughness = 1,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            }
        };
    }

    private static (Vector3 Left, Vector3 Right, Vector3 Normal) Edge(
        VisualRoute route, int index, float width, float height, float offset)
    {
        var vertex = route.Vertices[index];
        var pose = route.GetPoseAtDistance(vertex.DistanceMeters);
        var forward = ToGodot(pose.Forward);
        var horizontal = new Vector3(forward.X, 0, forward.Z).Normalized();
        if (horizontal.LengthSquared() < 0.001f) horizontal = Vector3.Forward;
        var right = horizontal.Cross(Vector3.Up).Normalized();
        var centre = ToGodot(vertex.Position) + Vector3.Up * height + right * offset;
        return (centre - right * width / 2, centre + right * width / 2,
            right.Cross(forward).Normalized());
    }

    private static void Vertex(SurfaceTool surface, Vector3 position, Vector3 normal)
    {
        surface.SetNormal(normal);
        surface.AddVertex(position);
    }

    public static Vector3 ToGodot(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
}
