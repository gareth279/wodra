using System;
using System.Collections.Generic;
using Godot;
using System.Linq;
using NVector3 = System.Numerics.Vector3;
using IndoorCycling.Core.Simulation;

/// <summary>Trail-only surface presentation. Route points remain the shared, authoritative centreline.</summary>
public static class TrailEnvironmentBuilder
{
    /// <summary>Visual gravel running surface. This authored fallback is not a survey of trail width.</summary>
    public const float RideableTrailWidthMeters = 2.8f;
    /// <summary>MTB ribbon width is an art choice. It is not a measurement of untagged trail sections.</summary>
    public const float MountainBikeRibbonWidthMeters = 1.55f;

    public static IReadOnlyList<MeshInstance3D> Build(VisualRoute route, RouteDiscipline discipline,
        IReadOnlyList<TrailRollerDefinition>? rollers = null)
    {
        var isMountainBike = discipline == RouteDiscipline.MountainBike;
        var width = isMountainBike ? MountainBikeRibbonWidthMeters : RideableTrailWidthMeters;
        var bedColour = isMountainBike ? new Color(0.24f, 0.22f, 0.17f) : new Color(0.31f, 0.28f, 0.22f);
        var surfaceColour = isMountainBike ? new Color(0.36f, 0.29f, 0.20f) : new Color(0.49f, 0.40f, 0.27f);
        var shoulder = ProceduralRoad.BuildRibbon(route, width + (isMountainBike ? 0.9f : 0.5f), -0.015f, bedColour);
        shoulder.Name = "TrailShoulderBed";
        shoulder.MaterialOverride = Material(bedColour);

        var surface = rollers is { Count: > 0 }
            ? BuildProfiledSurface(route, width, 0.012f, surfaceColour, rollers)
            : ProceduralRoad.BuildRibbon(route, width, 0.012f, surfaceColour);
        surface.Name = isMountainBike ? "MountainBikeSingletrackSurface" : "GravelTrailSurface";
        surface.MaterialOverride = Material(surfaceColour);

        return [shoulder, surface];
    }

    private static MeshInstance3D BuildProfiledSurface(VisualRoute route, float width, float height,
        Color color, IReadOnlyList<TrailRollerDefinition> rollers)
    {
        var centers = route.Samples.Select(sample => sample.Position + NVector3.UnitY * (float)RollerLift(sample.DistanceMeters)).ToArray();
        var quads = RoadRibbonMath.BuildQuads(centers, width);
        var surface = new SurfaceTool(); surface.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var quad in quads)
        {
            var up = NVector3.UnitY * height;
            Add(quad.StartLeft + up, quad.EndLeft + up, quad.StartRight + up);
            Add(quad.StartRight + up, quad.EndLeft + up, quad.EndRight + up);
        }
        var mesh = surface.Commit();
        return new MeshInstance3D { Name = "MountainBikeSingletrackSurface", Mesh = mesh, MaterialOverride = Material(color) };

        double RollerLift(double distance)
        {
            foreach (var roller in rollers)
            {
                if (distance < roller.StartMeters || distance > roller.EndMeters) continue;
                var t = (distance - roller.StartMeters) / (roller.EndMeters - roller.StartMeters);
                var peak = (roller.PeakMeters - roller.StartMeters) / (roller.EndMeters - roller.StartMeters);
                var lift = t <= peak
                    ? Math.Sin(Math.PI * .5 * Math.Clamp(t / peak, 0, 1))
                    : Math.Cos(Math.PI * .5 * Math.Clamp((t - peak) / (1 - peak), 0, 1));
                return lift * roller.HeightMeters;
            }
            return 0;
        }
        void Add(NVector3 a, NVector3 b, NVector3 c)
        {
            var normal = NVector3.Normalize(NVector3.Cross(b - a, c - a));
            if (normal.Y < 0) normal = -normal;
            surface.SetNormal(new Godot.Vector3(normal.X, normal.Y, normal.Z));
            surface.AddVertex(new Godot.Vector3(a.X, a.Y, a.Z));
            surface.SetNormal(new Godot.Vector3(normal.X, normal.Y, normal.Z));
            surface.AddVertex(new Godot.Vector3(b.X, b.Y, b.Z));
            surface.SetNormal(new Godot.Vector3(normal.X, normal.Y, normal.Z));
            surface.AddVertex(new Godot.Vector3(c.X, c.Y, c.Z));
        }
    }

    private static StandardMaterial3D Material(Color colour) => new()
    {
        AlbedoColor = colour,
        Roughness = 1,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled
    };
}
