using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

/// <summary>Pure geometry helpers for static background street ribbons.</summary>
public static class BackgroundStreetGeometry
{
    // The extractor clips route-parallel overlap within 8 m. Only repair the small
    // endpoint gap left by that clipping; long proximity snaps create false streets.
    public const float MaximumRouteConnectorExtensionMeters = 8.5f;
    public readonly record struct RouteConnection(Vector3 Position, int EndIndex, float DistanceMeters, double RouteDistanceMeters);
    public sealed record ConnectedStreet(Vector3[] Points, IReadOnlyList<RouteConnection> Connections);

    public static IReadOnlyList<RoadJunctionOpening> BuildJunctionOpenings(VisualRoute route,RouteBackgroundStreetData? data)
    {
        if(data is null)return [];
        var openings=new List<RoadJunctionOpening>();
        foreach(var street in data.Streets)
        {
            var points=ConvertPoints(route,street.Points);
            var connected=ConnectRouteEnds(points,route.Samples);
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
    public static TerrainRoadCorridor[] BuildTerrainCorridors(VisualRoute route,RouteBackgroundStreetData? data) =>
        data?.Streets.Select(street=>new TerrainRoadCorridor(
            ConnectRouteEnds(ConvertPoints(route,street.Points),route.Samples).Points,(float)street.WidthMeters)).ToArray() ?? [];

    /// <summary>
    /// Rejoins route-builder pieces whose sampled overlap was removed. Only endpoints whose
    /// outward heading points toward the route are extended, preventing nearby parallel streets
    /// from being pulled sideways into it.
    /// </summary>
    public static ConnectedStreet ConnectRouteEnds(IReadOnlyList<Vector3> street,
        IReadOnlyList<VisualRouteSample> routeSamples, float maximumExtensionMeters = MaximumRouteConnectorExtensionMeters)
    {
        if (street.Count < 2 || routeSamples.Count < 2 || !float.IsFinite(maximumExtensionMeters) || maximumExtensionMeters <= 0) return new(street.ToArray(), []);
        var points = street.ToArray();
        var result = new List<RouteConnection>(2);
        AddEnd(0, street[0] - street[1]);
        AddEnd(street.Count - 1, street[^1] - street[^2]);
        return new(points, result);

        void AddEnd(int endIndex, Vector3 outward)
        {
            var origin = points[endIndex];
            var nearest = NearestRoutePoint(origin, routeSamples);
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

    private static (Vector3 Position,double DistanceMeters) NearestRoutePoint(Vector3 point, IReadOnlyList<VisualRouteSample> samples)
    {
        var bestDistance = float.PositiveInfinity; var best = (samples[0].Position,samples[0].DistanceMeters);
        for (var i = 0; i < samples.Count - 1; i++)
        {
            var a = samples[i].Position; var b = samples[i + 1].Position;
            var segment = new Vector2(b.X - a.X, b.Z - a.Z);
            var lengthSquared = segment.LengthSquared();
            var t = lengthSquared > 1e-8f ? Math.Clamp(Vector2.Dot(new Vector2(point.X-a.X,point.Z-a.Z),segment)/lengthSquared,0,1) : 0;
            var candidate = Vector3.Lerp(a,b,t); var dx=point.X-candidate.X;var dz=point.Z-candidate.Z;var distance=dx*dx+dz*dz;
            if(distance<bestDistance){bestDistance=distance;best=(candidate,samples[i].DistanceMeters+(samples[i+1].DistanceMeters-samples[i].DistanceMeters)*t);}
        }
        return best;
    }
    public static double NearestRouteDistanceMeters(Vector3 point,IReadOnlyList<VisualRouteSample> samples)=>
        NearestRoutePoint(point,samples).DistanceMeters;

    /// <summary>True when a mapped street quad duplicates the selected route rather than crossing it.</summary>
    public static bool OverlapsRoute(RoadRibbonQuad quad,IReadOnlyList<VisualRouteSample> routeSamples,
        float streetWidth,float routeWidth=RoadRibbonMath.RoadWidthMeters)
    {
        var center=(quad.StartLeft+quad.EndLeft+quad.StartRight+quad.EndRight)*.25f;
        var nearest=NearestRoutePoint(center,routeSamples);
        var nearestDistance=Vector2.Distance(new(center.X,center.Z),new(nearest.Position.X,nearest.Position.Z));
        if(nearestDistance>(routeWidth+streetWidth)*.5f+.35f)return false;
        var streetDirection=new Vector2(quad.EndLeft.X-quad.StartLeft.X,quad.EndLeft.Z-quad.StartLeft.Z);
        var routeDirection=new Vector2(0,0);
        var best=float.PositiveInfinity;
        for(var i=0;i<routeSamples.Count-1;i++)
        {
            var a=routeSamples[i].Position;var b=routeSamples[i+1].Position;
            var segment=new Vector2(b.X-a.X,b.Z-a.Z);var length2=segment.LengthSquared();
            var t=length2>1e-8f?Math.Clamp(Vector2.Dot(new(center.X-a.X,center.Z-a.Z),segment)/length2,0,1):0;
            var p=Vector2.Lerp(new(a.X,a.Z),new(b.X,b.Z),t);var d=Vector2.DistanceSquared(new(center.X,center.Z),p);
            if(d<best){best=d;routeDirection=segment;}
        }
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
    private static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
    private static float TriangleAreaSquared(Vector3 a,Vector3 b,Vector3 c)=>Vector3.Cross(b-a,c-a).LengthSquared();
}
