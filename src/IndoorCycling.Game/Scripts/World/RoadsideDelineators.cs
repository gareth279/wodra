using Godot;

/// <summary>Static, route-spaced roadside markers rendered as two batched MultiMeshes.</summary>
public static class RoadsideDelineators
{
    public const double SpacingMeters = 10;
    private const float OffsetFromRoadEdgeMeters = 0.75f;

    public static Node3D Build(VisualRoute route)
    {
        var root = new Node3D { Name = "RoadsideDelineators" };
        var bodyTransforms = new System.Collections.Generic.List<Transform3D>();
        var reflectorTransforms = new System.Collections.Generic.List<Transform3D>();
        var sideOffset = ProceduralRoad.WidthMeters / 2 + OffsetFromRoadEdgeMeters;

        // Omit the endpoint: it aliases distance zero on a loop.
        for (double distance = 0; distance < route.LengthMeters; distance += SpacingMeters)
        {
            var pose = route.GetPoseAtDistance(distance);
            var forward = ProceduralRoad.ToGodot(pose.Forward);
            var horizontalForward = new Vector3(forward.X, 0, forward.Z).Normalized();
            if (horizontalForward.LengthSquared() < 0.001f)
                horizontalForward = Vector3.Forward;
            var right = horizontalForward.Cross(Vector3.Up).Normalized();
            var centre = ProceduralRoad.ToGodot(pose.Position) + Vector3.Up * 0.02f;

            foreach (var side in new[] { -1f, 1f })
            {
                var basePosition = centre + right * side * sideOffset;
                bodyTransforms.Add(new Transform3D(Basis.Identity, basePosition));
                reflectorTransforms.Add(new Transform3D(Basis.Identity, basePosition + Vector3.Up * 0.66f));
            }
        }

        root.AddChild(CreateBatch(
            "WhitePosts", new CylinderMesh { TopRadius = 0.065f, BottomRadius = 0.075f, Height = 0.9f },
            new StandardMaterial3D { AlbedoColor = new Color(0.92f, 0.94f, 0.9f), Roughness = 0.8f },
            bodyTransforms));
        root.AddChild(CreateBatch(
            "RedReflectors", new CylinderMesh { TopRadius = 0.071f, BottomRadius = 0.071f, Height = 0.12f },
            new StandardMaterial3D
            {
                AlbedoColor = new Color(0.95f, 0.08f, 0.035f),
                Roughness = 0.55f,
                EmissionEnabled = true,
                Emission = new Color(0.35f, 0.025f, 0.01f)
            },
            reflectorTransforms));
        return root;
    }

    private static MultiMeshInstance3D CreateBatch(
        string name, Mesh mesh, Material material,
        System.Collections.Generic.IReadOnlyList<Transform3D> transforms)
    {
        var multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = transforms.Count
        };
        for (var i = 0; i < transforms.Count; i++)
            multimesh.SetInstanceTransform(i, transforms[i]);

        return new MultiMeshInstance3D
        {
            Name = name,
            Multimesh = multimesh,
            MaterialOverride = material
        };
    }
}
