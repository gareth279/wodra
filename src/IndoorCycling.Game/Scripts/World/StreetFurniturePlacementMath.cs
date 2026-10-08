using System;
using System.Numerics;

public readonly record struct StreetFurnitureVisualPlacement(
    Vector3 SourcePosition,Vector3 VisualPosition,bool ProxySuppressed,bool Relocated,bool RetainedInRoad);

/// <summary>Keeps source OSM location separate from category-aware visual proxy placement.</summary>
public static class StreetFurniturePlacementMath
{
    public const float RoadsideClearanceMeters=.75f;

    public static StreetFurnitureVisualPlacement Place(string category,Vector3 sourcePosition,Vector3 nearestRoutePosition,
        Vector3 routeForward,double sourceDistanceFromRouteMeters,float roadWidthMeters)
    {
        if(string.IsNullOrWhiteSpace(category)||!Finite(sourcePosition)||!Finite(nearestRoutePosition)
            ||!double.IsFinite(sourceDistanceFromRouteMeters)||sourceDistanceFromRouteMeters<0
            ||!float.IsFinite(roadWidthMeters)||roadWidthMeters<=0)throw new ArgumentOutOfRangeException(nameof(sourceDistanceFromRouteMeters));
        var inside=sourceDistanceFromRouteMeters<roadWidthMeters/2;
        if(category=="crossing")return new(sourcePosition,sourcePosition,true,false,false);
        var relocate=inside&&(category is "traffic-signal" or "stop-sign" or "give-way-sign" or "bench" or "bus-stop");
        if(!relocate)return new(sourcePosition,sourcePosition,false,false,inside);
        var visual=nearestRoutePosition+RiderLaneMath.LocalLeft(routeForward)*(roadWidthMeters/2+RoadsideClearanceMeters);
        return new(sourcePosition,visual,false,true,false);
    }

    private static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
}
