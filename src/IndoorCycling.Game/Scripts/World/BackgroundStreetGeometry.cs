using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

/// <summary>Pure geometry helpers for static background street ribbons.</summary>
public static class BackgroundStreetGeometry
{
    public static string MaterialGroupForHighway(string highway) => highway switch
    {
        "motorway" => "motorway", "trunk" => "trunk", "primary" => "primary", "secondary" => "secondary",
        "footway" or "path" => "path", "pedestrian" => "pedestrian", "cycleway" => "cycleway", _ => "local"
    };

    /// <summary>Keeps surface data and its accounting value under the same chunk/material key.</summary>
    public sealed class MaterialChunkBucketRegistry<TMaterial, TBucket> where TMaterial : notnull
    {
        private readonly Dictionary<(int Chunk, TMaterial Material), TBucket> _items = [];
        public IReadOnlyDictionary<(int Chunk, TMaterial Material), TBucket> Items => _items;
        public TBucket GetOrAdd(int chunk, TMaterial material, Func<TBucket> create)
        {
            var key = (chunk, material);
            if (_items.TryGetValue(key, out var existing)) return existing;
            var bucket = create();
            _items.Add(key, bucket);
            return bucket;
        }
    }

    // The extractor clips route-parallel overlap within 8 m. Only repair the small
    // endpoint gap left by that clipping; long proximity snaps create false streets.
    public const float MaximumRouteConnectorExtensionMeters = 8.5f;
    public readonly record struct RouteConnection(Vector3 Position, int EndIndex, float DistanceMeters, double RouteDistanceMeters);
    public sealed record ConnectedStreet(Vector3[] Points, IReadOnlyList<RouteConnection> Connections);

    /// <summary>Exact nearest-segment queries over route samples using a small spatial hash.</summary>
    public sealed class RouteSegmentSpatialIndex
    {
        private const float CellMeters = 64;
        private readonly record struct Segment(int Index, Vector3 A, Vector3 B, double DistanceA, double DistanceB);
        private readonly Dictionary<(int X, int Z), List<Segment>> _cells = [];
        private readonly Segment[] _segments;

        public RouteSegmentSpatialIndex(IReadOnlyList<VisualRouteSample> samples)
        {
            if (samples.Count < 2) throw new ArgumentException("At least two route samples are required.", nameof(samples));
            _segments = new Segment[samples.Count - 1];
            for (var i = 0; i < _segments.Length; i++)
            {
                var segment = new Segment(i, samples[i].Position, samples[i + 1].Position,
                    samples[i].DistanceMeters, samples[i + 1].DistanceMeters);
                _segments[i] = segment;
                var minX = Cell(segment.A.X); var maxX = Cell(segment.B.X);
                var minZ = Cell(segment.A.Z); var maxZ = Cell(segment.B.Z);
                for (var x = Math.Min(minX, maxX); x <= Math.Max(minX, maxX); x++)
                for (var z = Math.Min(minZ, maxZ); z <= Math.Max(minZ, maxZ); z++)
                {
                    if (!_cells.TryGetValue((x, z), out var entries)) _cells[(x, z)] = entries = [];
                    entries.Add(segment);
                }
            }
        }

        public (Vector3 Position, double DistanceMeters, Vector2 Direction, float DistanceSquared) Nearest(Vector3 point)
        {
            var centerX = Cell(point.X); var centerZ = Cell(point.Z);
            var considered = new HashSet<int>();
            var bestDistance = float.PositiveInfinity;
            var best = (_segments[0].A, _segments[0].DistanceA, Vector2.Zero, float.PositiveInfinity);
            for (var radius = 0; ; radius++)
            {
                for (var x = centerX - radius; x <= centerX + radius; x++)
                for (var z = centerZ - radius; z <= centerZ + radius; z++)
                {
                    if (radius > 0 && x != centerX - radius && x != centerX + radius
                        && z != centerZ - radius && z != centerZ + radius) continue;
                    if (!_cells.TryGetValue((x, z), out var entries)) continue;
                    foreach (var segment in entries)
                    {
                        if (!considered.Add(segment.Index)) continue;
                        var edge = new Vector2(segment.B.X - segment.A.X, segment.B.Z - segment.A.Z);
                        var lengthSquared = edge.LengthSquared();
                        var t = lengthSquared > 1e-8f
                            ? Math.Clamp(Vector2.Dot(new Vector2(point.X - segment.A.X, point.Z - segment.A.Z), edge) / lengthSquared, 0, 1)
                            : 0;
                        var candidate = Vector3.Lerp(segment.A, segment.B, t);
                        var delta = new Vector2(point.X - candidate.X, point.Z - candidate.Z);
                        var distanceSquared = delta.LengthSquared();
                        if (distanceSquared >= bestDistance) continue;
                        bestDistance = distanceSquared;
                        best = (candidate, segment.DistanceA + (segment.DistanceB - segment.DistanceA) * t, edge, distanceSquared);
                    }
                }

                var left = (centerX - radius) * CellMeters;
                var right = (centerX + radius + 1) * CellMeters;
                var top = (centerZ - radius) * CellMeters;
                var bottom = (centerZ + radius + 1) * CellMeters;
                var outsideDistance = Math.Min(Math.Min(point.X - left, right - point.X),
                    Math.Min(point.Z - top, bottom - point.Z));
                if (bestDistance <= outsideDistance * outsideDistance) return best;
            }
        }

        private static int Cell(float coordinate) => (int)MathF.Floor(coordinate / CellMeters);
    }

    public static IReadOnlyList<RoadJunctionOpening> BuildJunctionOpenings(VisualRoute route,RouteBackgroundStreetData? data,
        RouteSegmentSpatialIndex? spatialIndex = null)
    {
        if(data is null)return [];
        spatialIndex ??= new RouteSegmentSpatialIndex(route.Samples);
        var openings=new List<RoadJunctionOpening>();
        foreach(var street in data.Streets)
        {
            var points=ConvertPoints(route,street.Points);
            var connected=ConnectRouteEnds(points,route.Samples,spatialIndex:spatialIndex);
            openings.AddRange(connected.Connections.Select(connection=>new RoadJunctionOpening(
                connection.RouteDistanceMeters,(float)street.WidthMeters*.5f+.75f)));
        }
        return openings.GroupBy(opening=>(long)Math.Round(opening.RouteDistanceMeters))
            .Select(group=>new RoadJunctionOpening(group.Average(item=>item.RouteDistanceMeters),group.Max(item=>item.RadiusMeters)))
            .OrderBy(opening=>opening.RouteDistanceMeters).ToArray();
    }

    public static Vector3[] ConvertPoints(VisualRoute route,IReadOnlyList<RouteBackgroundStreetPoint> points) =>
        points.Select(p=>route.GetPositionForCoordinate(p.Latitude,p.Longitude,p.ElevationMeters)).ToArray();

    /// <summary>Uses the exact same restored centerlines for rendering and terrain road cutouts.</summary>
    public static TerrainRoadCorridor[] BuildTerrainCorridors(VisualRoute route,RouteBackgroundStreetData? data,
        RouteSegmentSpatialIndex? spatialIndex = null)
    {
        if (data is null) return [];
        spatialIndex ??= new RouteSegmentSpatialIndex(route.Samples);
        return data.Streets.Select(street=>new TerrainRoadCorridor(
            ConnectRouteEnds(ConvertPoints(route,street.Points),route.Samples,spatialIndex:spatialIndex).Points,
            (float)street.WidthMeters, street.OsmId)).ToArray();
    }

    /// <summary>
    /// Rejoins route-builder pieces whose sampled overlap was removed. Only endpoints whose
    /// outward heading points toward the route are extended, preventing nearby parallel streets
    /// from being pulled sideways into it.
    /// </summary>
    public static ConnectedStreet ConnectRouteEnds(IReadOnlyList<Vector3> street,
        IReadOnlyList<VisualRouteSample> routeSamples, float maximumExtensionMeters = MaximumRouteConnectorExtensionMeters,
        RouteSegmentSpatialIndex? spatialIndex = null)
    {
        if (street.Count < 2 || routeSamples.Count < 2 || !float.IsFinite(maximumExtensionMeters) || maximumExtensionMeters <= 0) return new(street.ToArray(), []);
        spatialIndex ??= new RouteSegmentSpatialIndex(routeSamples);
        var points = street.ToArray();
        var result = new List<RouteConnection>(2);
        AddEnd(0, street[0] - street[1]);
        AddEnd(street.Count - 1, street[^1] - street[^2]);
        return new(points, result);

        void AddEnd(int endIndex, Vector3 outward)
        {
            var origin = points[endIndex];
            var nearest = spatialIndex.Nearest(origin);
            var delta = nearest.Position - origin;
            var distance = new Vector2(delta.X, delta.Z).Length();
            if (distance < 1 || distance > maximumExtensionMeters || outward.LengthSquared() < 1e-6f) return;
            var direction = Vector2.Normalize(new Vector2(delta.X, delta.Z));
            var heading = Vector2.Normalize(new Vector2(outward.X, outward.Z));
            if (Vector2.Dot(heading,direction) < 0.35f) return;
            points[endIndex] = nearest.Position + Vector3.UnitY * 0.015f;
            result.Add(new RouteConnection(nearest.Position, endIndex, distance,nearest.DistanceMeters));
        }
    }

    public static double NearestRouteDistanceMeters(Vector3 point,IReadOnlyList<VisualRouteSample> samples,
        RouteSegmentSpatialIndex? spatialIndex = null) =>
        (spatialIndex ?? new RouteSegmentSpatialIndex(samples)).Nearest(point).DistanceMeters;

    /// <summary>True when a mapped street quad duplicates the selected route rather than crossing it.</summary>
    public static bool OverlapsRoute(RoadRibbonQuad quad,IReadOnlyList<VisualRouteSample> routeSamples,
        float streetWidth,float routeWidth=RoadRibbonMath.RoadWidthMeters,
        RouteSegmentSpatialIndex? spatialIndex = null)
    {
        var center=(quad.StartLeft+quad.EndLeft+quad.StartRight+quad.EndRight)*.25f;
        var nearest=(spatialIndex ?? new RouteSegmentSpatialIndex(routeSamples)).Nearest(center);
        var nearestDistance=MathF.Sqrt(nearest.DistanceSquared);
        if(nearestDistance>(routeWidth+streetWidth)*.5f+.35f)return false;
        var streetDirection=new Vector2(quad.EndLeft.X-quad.StartLeft.X,quad.EndLeft.Z-quad.StartLeft.Z);
        var routeDirection=nearest.Direction;
        return streetDirection.LengthSquared()>1e-8f&&routeDirection.LengthSquared()>1e-8f
            &&Math.Abs(Vector2.Dot(Vector2.Normalize(streetDirection),Vector2.Normalize(routeDirection)))>=.82f;
    }
    public static int RouteChunkIndex(double routeDistanceMeters,double routeLengthMeters,double chunkMeters=400)
    {
        if(!double.IsFinite(routeDistanceMeters)||!double.IsFinite(routeLengthMeters)||!double.IsFinite(chunkMeters)||routeLengthMeters<=0||chunkMeters<=0)
            throw new ArgumentOutOfRangeException(nameof(routeDistanceMeters));
        var last=Math.Max(0,(int)Math.Ceiling(routeLengthMeters/chunkMeters)-1);
        return Math.Clamp((int)(Math.Clamp(routeDistanceMeters,0,routeLengthMeters-.001)/chunkMeters),0,last);
    }

    public static IReadOnlyList<RoadRibbonQuad> BuildRibbon(IReadOnlyList<Vector3> positions,float width)
    {
        if(positions.Count<2||!float.IsFinite(width)||width<=0)return [];
        // Background ways are open OSM polylines. The canonical route helper assumes a
        // closed loop and wraps its end tangent, which creates malformed caps and spikes.
        var sections=new (Vector3 Left,Vector3 Right)[positions.Count];
        for(var i=0;i<positions.Count;i++)
        {
            var current=positions[i];
            var previous=i==0?current-(positions[1]-current):positions[i-1];
            var next=i==positions.Count-1?current+(current-positions[i-1]):positions[i+1];
            sections[i]=(current+RoadRibbonMath.OffsetAtJoin(previous,current,next,-width*.5f),
                current+RoadRibbonMath.OffsetAtJoin(previous,current,next,width*.5f));
        }
        var source=new RoadRibbonQuad[positions.Count-1];
        for(var i=0;i<source.Length;i++)source[i]=new(sections[i].Left,sections[i+1].Left,sections[i].Right,sections[i+1].Right);
        return source.Where(q=>Finite(q.StartLeft)&&Finite(q.EndLeft)&&Finite(q.StartRight)&&Finite(q.EndRight)
            &&TriangleAreaSquared(q.StartLeft,q.EndLeft,q.StartRight)>1e-8f
            &&TriangleAreaSquared(q.EndLeft,q.EndRight,q.StartRight)>1e-8f).ToArray();
    }
    public readonly record struct SurfaceTriangle(Vector3 A, Vector3 B, Vector3 C);

    /// <summary>Returns top-facing triangles with upward normals in Wodra's +Y-up world.</summary>
    public static (SurfaceTriangle First, SurfaceTriangle Second) BuildTopFaceTriangles(RoadRibbonQuad quad) =>
        (new SurfaceTriangle(quad.StartLeft, quad.EndLeft, quad.StartRight),
         new SurfaceTriangle(quad.StartRight, quad.EndLeft, quad.EndRight));

    private static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
    private static float TriangleAreaSquared(Vector3 a,Vector3 b,Vector3 c)=>Vector3.Cross(b-a,c-a).LengthSquared();
}
