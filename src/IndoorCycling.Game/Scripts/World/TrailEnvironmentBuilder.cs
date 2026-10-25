using System.Collections.Generic;
using Godot;

/// <summary>Trail-only surface presentation. Route points remain the shared, authoritative centreline.</summary>
public static class TrailEnvironmentBuilder
{
    /// <summary>Visual gravel running surface. This authored fallback is not a survey of trail width.</summary>
    public const float RideableTrailWidthMeters = 2.8f;
    /// <summary>MTB ribbon width is an art choice. It is not a measurement of untagged trail sections.</summary>
    public const float MountainBikeRibbonWidthMeters = 1.55f;

    public static IReadOnlyList<MeshInstance3D> Build(VisualRoute route, RouteDiscipline discipline)
    {
        var isMountainBike = discipline == RouteDiscipline.MountainBike;
        var width = isMountainBike ? MountainBikeRibbonWidthMeters : RideableTrailWidthMeters;
        var bedColour = isMountainBike ? new Color(0.24f, 0.22f, 0.17f) : new Color(0.31f, 0.28f, 0.22f);
        var surfaceColour = isMountainBike ? new Color(0.36f, 0.29f, 0.20f) : new Color(0.49f, 0.40f, 0.27f);
        var shoulder = ProceduralRoad.BuildRibbon(route, width + (isMountainBike ? 0.9f : 0.5f), -0.015f, bedColour);
        shoulder.Name = "TrailShoulderBed";
        shoulder.MaterialOverride = Material(bedColour);

        var surface = ProceduralRoad.BuildRibbon(route, width, 0.012f, surfaceColour);
        surface.Name = isMountainBike ? "MountainBikeSingletrackSurface" : "GravelTrailSurface";
        surface.MaterialOverride = Material(surfaceColour);

        return [shoulder, surface];
    }

    private static StandardMaterial3D Material(Color colour) => new()
    {
        AlbedoColor = colour,
        Roughness = 1,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled
    };
}
