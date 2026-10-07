using System.Collections.Generic;
using System.Linq;
using System.Numerics;

/// <summary>Pure geometry helpers for static background street ribbons.</summary>
public static class BackgroundStreetGeometry
{
    public static Vector3[] ConvertPoints(VisualRoute route,IReadOnlyList<RouteBackgroundStreetPoint> points) =>
        points.Select(p=>route.GetPositionForCoordinate(p.Latitude,p.Longitude,p.ElevationMeters)).ToArray();

    public static IReadOnlyList<RoadRibbonQuad> BuildRibbon(IReadOnlyList<Vector3> positions,float width)
    {
        if(positions.Count<2||!float.IsFinite(width)||width<=0)return [];
        var source=RoadRibbonMath.BuildQuads(positions,width);
        return source.Where(q=>Finite(q.StartLeft)&&Finite(q.EndLeft)&&Finite(q.StartRight)&&Finite(q.EndRight)
            &&TriangleAreaSquared(q.StartLeft,q.EndLeft,q.StartRight)>1e-8f
            &&TriangleAreaSquared(q.EndLeft,q.EndRight,q.StartRight)>1e-8f).ToArray();
    }
    private static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
    private static float TriangleAreaSquared(Vector3 a,Vector3 b,Vector3 c)=>Vector3.Cross(b-a,c-a).LengthSquared();
}
