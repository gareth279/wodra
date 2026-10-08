using System;
using System.Numerics;

/// <summary>Visual rider lane placement in the VisualRoute east/up/south world frame.</summary>
public static class RiderLaneMath
{
    public const float RiderLateralOffsetMeters = 1.75f;

    public static Vector3 LocalLeft(Vector3 routeForward)
    {
        if (!Finite(routeForward)) throw new ArgumentOutOfRangeException(nameof(routeForward));
        var left=Vector3.Cross(Vector3.UnitY,routeForward);
        if(left.LengthSquared()<1e-8f)throw new ArgumentException("Route tangent must have a horizontal component.",nameof(routeForward));
        return Vector3.Normalize(left);
    }

    public static Vector3 OffsetPosition(Vector3 routePosition,Vector3 routeForward,float lateralOffsetMeters=RiderLateralOffsetMeters)
    {
        if(!Finite(routePosition)||!float.IsFinite(lateralOffsetMeters))throw new ArgumentOutOfRangeException(nameof(lateralOffsetMeters));
        return routePosition+LocalLeft(routeForward)*lateralOffsetMeters;
    }

    private static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
}
