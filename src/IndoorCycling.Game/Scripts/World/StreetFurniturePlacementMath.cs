using System;
using System.Numerics;

public readonly record struct StreetFurnitureVisualPlacement(
    Vector3 SourcePosition,Vector3 VisualPosition,bool ProxySuppressed,bool Relocated,bool RetainedInRoad)
{
    public bool ClearanceViolationDetected { get; init; }
    public bool ClearanceUnresolved { get; init; }
}

/// <summary>Keeps source OSM location separate from category-aware visual proxy placement.</summary>
public static class StreetFurniturePlacementMath
{
    public const float RoadsideClearanceMeters=.75f;

    public static StreetFurnitureVisualPlacement Place(string category,Vector3 sourcePosition,Vector3 nearestRoutePosition,
        Vector3 routeForward,double sourceDistanceFromRouteMeters,float roadWidthMeters,
        VisualRoute? route = null,double chainageMeters = 0,RoadCorridorGeometry? corridor = null)
    {
        if(string.IsNullOrWhiteSpace(category)||!Finite(sourcePosition)||!Finite(nearestRoutePosition)
            ||!double.IsFinite(sourceDistanceFromRouteMeters)||sourceDistanceFromRouteMeters<0
            ||!float.IsFinite(roadWidthMeters)||roadWidthMeters<=0)throw new ArgumentOutOfRangeException(nameof(sourceDistanceFromRouteMeters));
        var inside=sourceDistanceFromRouteMeters<roadWidthMeters/2;
        if(category=="crossing")return new(sourcePosition,sourcePosition,true,false,false);
        var canRelocate=category is "traffic-signal" or "stop-sign" or "give-way-sign" or "bench" or "bus-stop";
        var lateral=0f; var forward=0f; var side=RiderLaneMath.LocalLeft(routeForward);
        if(route is not null&&corridor is not null)
        {
            var relative=sourcePosition-nearestRoutePosition;
            lateral=Vector3.Dot(relative,side); forward=Vector3.Dot(relative,Vector3.Normalize(routeForward));
            var bounds=Footprint(category);
            var measured=corridor.Measure(route,chainageMeters,lateral,forward,bounds.Length,bounds.Width);
            inside=measured.Intersects||measured.ClearanceMeters<RoadCorridorGeometry.RoadsideClearanceMeters;
            if(!inside)return new(sourcePosition,sourcePosition,false,false,false);
        }
        var relocate=inside&&canRelocate;
        if(!relocate)return new(sourcePosition,sourcePosition,false,false,inside)
            { ClearanceViolationDetected=inside, ClearanceUnresolved=inside };
        Vector3 visual;
        if(route is not null&&corridor is not null)
        {
            var bounds=Footprint(category);
            var placement=corridor.FindSafeRoutePlacement(route,chainageMeters,lateral,forward,
                bounds.Length,bounds.Width,maximumShiftMeters: category == "bus-stop" ? 24 : 12);
            if(!placement.Resolved)return new(sourcePosition,sourcePosition,false,false,true)
                { ClearanceViolationDetected=true, ClearanceUnresolved=true };
            visual=nearestRoutePosition+side*placement.VisualLateralMeters
                +Vector3.Normalize(routeForward)*placement.VisualForwardMeters;
            visual.Y=sourcePosition.Y;
        }
        else visual=nearestRoutePosition+side*(roadWidthMeters/2+RoadsideClearanceMeters);
        return new(sourcePosition,visual,false,true,false) { ClearanceViolationDetected=true };
    }

    public static RoadFootprintDimensions FootprintMetersForCategory(string category)=>category switch
    {
        "bench"=>new(1.6f,.65f),"bus-stop"=>new(3.5f,1.8f),
        "traffic-signal"=>new(.5f,.5f),"stop-sign" or "give-way-sign"=>new(.8f,.45f),
        _=>new(.6f,.6f)
    };

    private static (float Length,float Width) Footprint(string category)
    {var dimensions=FootprintMetersForCategory(category);return(dimensions.LengthMeters,dimensions.WidthMeters);}

    private static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
}
