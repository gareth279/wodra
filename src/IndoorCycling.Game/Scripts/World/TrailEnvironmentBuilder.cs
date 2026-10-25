using System.Collections.Generic;
using Godot;

/// <summary>Trail-only surface presentation. Route points remain the shared, authoritative centreline.</summary>
public static class TrailEnvironmentBuilder
{
    /// <summary>Visual gravel running surface. This authored fallback is not a survey of trail width.</summary>
    public const float RideableTrailWidthMeters = 2.8f;

    public static IReadOnlyList<MeshInstance3D> Build(VisualRoute route)
    {
        var shoulder = ProceduralRoad.BuildRibbon(route, RideableTrailWidthMeters + 0.5f, -0.015f,
            new Color(0.31f, 0.28f, 0.22f));
        shoulder.Name = "TrailShoulderBed";
        shoulder.MaterialOverride = Material(new Color(0.31f, 0.28f, 0.22f));

        var surface = ProceduralRoad.BuildRibbon(route, RideableTrailWidthMeters, 0.012f,
            new Color(0.49f, 0.40f, 0.27f));
        surface.Name = "GravelTrailSurface";
        surface.MaterialOverride = Material(new Color(0.49f, 0.40f, 0.27f));

        return [shoulder, surface];
    }

    private static StandardMaterial3D Material(Color colour) => new()
    {
        AlbedoColor = colour,
        Roughness = 1,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled
    };
}
