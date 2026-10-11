using System.Collections.Generic;
using System.Linq;
using Godot;

public static class ProceduralRoad
{
    public const float WidthMeters = RoadRibbonMath.RoadWidthMeters;
    public const double NonAsphaltJoinExclusionMeters = 6;

#if DEBUG
    // Temporary editor/debug-build switches for isolating the three seam surfaces.
    public static bool DebugDisableFootpaths { get; set; }
    public static bool DebugDisableKerbs { get; set; }
    public static bool DebugDisableAsphaltSeamFill { get; set; }
#endif

    public static MeshInstance3D BuildRibbon(VisualRoute route, float width, float height, Color colour, float offset = 0)
    {
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        var positions = route.Samples.Select(sample => sample.Position).ToArray();
        var quads = RoadRibbonMath.BuildQuads(positions, width, offset);
        for (var i = 0; i < quads.Count; i++)
        {
            var quad = quads[i];
            var up = Vector3.Up * height;
            AddQuad(surface, ToGodot(quad.StartLeft) + up, ToGodot(quad.EndLeft) + up,
                ToGodot(quad.StartRight) + up, ToGodot(quad.EndRight) + up, SegmentNormal(route, i));
        }
        return CreateSurface("RoadRibbon", surface, colour);
    }

    /// <summary>Small asphalt pads join only sharp route discontinuities; no offset ribbon crosses the corner.</summary>
    public static MeshInstance3D? BuildSharpJoinFills(VisualRoute route, float roadWidth, Color colour,
        bool enabled = true)
    {
#if DEBUG
        enabled &= !DebugDisableAsphaltSeamFill;
#endif
        if (!enabled) return null;
        var last = route.Samples.Count - 1;
        var sharp = new List<int>();
        for (var i = 0; i < last; i++)
        {
            var (previous, next) = Neighbours(route, i);
            if (JoinType(previous, route.Samples[i], next) == RoadJoinType.Discontinuity)
                sharp.Add(i);
        }
        if (sharp.Count == 0) return null;

        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var index in sharp)
        {
            var center = ToGodot(route.Samples[index].Position) + Vector3.Up * 0.012f;
            var radius=RoadRibbonMath.SharpJoinFillRadius(
                route.Samples[index==0?last-1:index-1].Position,route.Samples[index].Position,
                route.Samples[index+1].Position,roadWidth);
            foreach (var triangle in RoadRibbonMath.BuildSharpJoinFill(ToNumerics(center), radius))
            {
                Vertex(surface, ToGodot(triangle.A), Vector3.Up);
                Vertex(surface, ToGodot(triangle.B), Vector3.Up);
                Vertex(surface, ToGodot(triangle.C), Vector3.Up);
            }
        }
        return CreateSurface("SharpJoinRoadFill", surface, colour);
    }

    public static Node3D BuildKerbsAndFootpaths(VisualRoute route, bool includeKerbs = true,
        bool includeFootpaths = true, IReadOnlyList<RoadJunctionOpening>? junctionOpenings = null)
    {
#if DEBUG
        includeKerbs &= !DebugDisableKerbs;
        includeFootpaths &= !DebugDisableFootpaths;
#endif
        var edges = new Node3D { Name = "KerbsAndFootpaths" };
        var kerbFace = new Color(0.48f, 0.49f, 0.47f);
        var kerbTop = new Color(0.62f, 0.63f, 0.60f);
        var footpath = new Color(0.47f, 0.48f, 0.45f);
        foreach (float side in new[] { -1f, 1f })
        {
            if (includeKerbs)
            {
                edges.AddChild(BuildProfileRibbon(route, side * 3.5f, side * 3.5f, 0.005f, 0.14f, kerbFace,junctionOpenings));
                edges.AddChild(BuildProfileRibbon(route, side * 3.5f, side * 3.73f, 0.14f, 0.14f, kerbTop,junctionOpenings));
            }
            if (includeFootpaths)
                edges.AddChild(BuildProfileRibbon(route, side * 3.73f, side * 6.0f, 0.14f, 0.11f, footpath,junctionOpenings));
        }
        return edges;
    }

    private static MeshInstance3D BuildProfileRibbon(VisualRoute route, float lateralStart, float lateralEnd,
        float startHeight, float endHeight, Color colour,IReadOnlyList<RoadJunctionOpening>? junctionOpenings)
    {
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        var positions = route.Samples.Select(sample => ToNumerics(sample.Position)).ToArray();
        var distances = route.Samples.Select(sample => sample.DistanceMeters).ToArray();
        var quads = RoadRibbonMath.BuildProfileQuads(positions, distances,
            lateralStart, lateralEnd, startHeight, endHeight, NonAsphaltJoinExclusionMeters);
        foreach (var sourceQuad in quads)
        foreach (var profileQuad in RoadRibbonMath.ClipProfileQuadAtJunctions(sourceQuad,distances[^1],junctionOpenings))
        {
            var quad = profileQuad.Quad;
            var forward = ToGodot(positions[profileQuad.SourceSegmentIndex + 1]
                - positions[profileQuad.SourceSegmentIndex]).Normalized();
            var normal = (ToGodot(quad.StartRight) - ToGodot(quad.StartLeft)).Cross(forward).Normalized();
            if (normal.LengthSquared() < 0.001f) normal = Vector3.Up;
            AddQuad(surface, ToGodot(quad.StartLeft), ToGodot(quad.EndLeft),
                ToGodot(quad.StartRight), ToGodot(quad.EndRight), normal);
        }
        return CreateSurface("RouteEdge", surface, colour);
    }

    private static void AddQuad(SurfaceTool surface, Vector3 aLeft, Vector3 bLeft,
        Vector3 aRight, Vector3 bRight, Vector3 normal)
    {
        Vertex(surface, aLeft, normal);
        Vertex(surface, bLeft, normal);
        Vertex(surface, aRight, normal);
        Vertex(surface, aRight, normal);
        Vertex(surface, bLeft, normal);
        Vertex(surface, bRight, normal);
    }

    private static (VisualRouteSample Previous, VisualRouteSample Next) Neighbours(VisualRoute route, int index)
    {
        var last = route.Samples.Count - 1;
        return (route.Samples[index == 0 ? last - 1 : index - 1],
            route.Samples[index == last ? 1 : index + 1]);
    }

    private static Vector3 MiterOffset(VisualRouteSample previous, VisualRouteSample current,
        VisualRouteSample next, float distance, bool segmentStart) => ToGodot(RoadRibbonMath.OffsetAtJoin(
            ToNumerics(previous.Position), ToNumerics(current.Position), ToNumerics(next.Position), distance, segmentStart));

    private static RoadJoinType JoinType(VisualRouteSample previous, VisualRouteSample current, VisualRouteSample next) =>
        RoadRibbonMath.ClassifyJoin(ToNumerics(previous.Position), ToNumerics(current.Position), ToNumerics(next.Position));

    private static bool IsDiscontinuity(VisualRoute route, int index)
    {
        var (previous, next) = Neighbours(route, index);
        return JoinType(previous, route.Samples[index], next) == RoadJoinType.Discontinuity;
    }

    private static Vector3 SegmentForward(VisualRoute route, int index, bool segmentStart = true)
    {
        var from = route.Samples[index].Position;
        var to = route.Samples[segmentStart ? index + 1 : index - 1].Position;
        return (ToGodot(to) - ToGodot(from)).Normalized();
    }

    private static Vector3 SegmentNormal(VisualRoute route, int index)
    {
        var forward = SegmentForward(route, index);
        var horizontal = new Vector3(forward.X, 0, forward.Z).Normalized();
        return horizontal.Cross(Vector3.Up).Cross(forward).Normalized();
    }

    private static MeshInstance3D CreateSurface(string name, SurfaceTool surface, Color colour) => new()
    {
        Name = name,
        Mesh = surface.Commit(),
        MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = colour, Roughness = 1, CullMode = BaseMaterial3D.CullModeEnum.Disabled
        }
    };

    private static void Vertex(SurfaceTool surface, Vector3 position, Vector3 normal)
    {
        surface.SetNormal(normal);
        surface.AddVertex(position);
    }

    private static System.Numerics.Vector3 ToNumerics(Vector3 vector) => new(vector.X, vector.Y, vector.Z);
    private static System.Numerics.Vector3 ToNumerics(System.Numerics.Vector3 vector) => vector;
    public static Vector3 ToGodot(System.Numerics.Vector3 vector) => new(vector.X, vector.Y, vector.Z);
}
