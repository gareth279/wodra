using System;
using System.Numerics;

/// <summary>Deterministic route-frame transforms for route-authored scenery in Godot's east/up/south basis.</summary>
public static class RouteSceneTransformMath
{
    public readonly record struct YieldTriangleVertices(Vector3 LeftBase, Vector3 RightBase, Vector3 Apex);

    public static Vector3 HorizontalForward(Vector3 routeForward)
    {
        if (!Finite(routeForward)) throw new ArgumentOutOfRangeException(nameof(routeForward));
        routeForward.Y = 0;
        if (routeForward.LengthSquared() < 1e-8f)
            throw new ArgumentException("Route tangent must have a horizontal component.", nameof(routeForward));
        return Vector3.Normalize(routeForward);
    }

    /// <summary>Positive lateral is road-right; positive forward offset follows the route tangent.</summary>
    public static Vector3 Position(Vector3 routePosition, Vector3 routeForward, float lateralMeters, float forwardMeters)
    {
        var forward = HorizontalForward(routeForward);
        if (!Finite(routePosition) || !float.IsFinite(lateralMeters) || !float.IsFinite(forwardMeters))
            throw new ArgumentOutOfRangeException(nameof(routePosition));
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        return routePosition + right * lateralMeters + forward * forwardMeters;
    }

    /// <summary>Godot local -Z is the observed direction of travel.</summary>
    public static float YawRadians(Vector3 routeForward)
    {
        var forward = HorizontalForward(routeForward);
        return MathF.Atan2(-forward.X, -forward.Z);
    }

    /// <summary>The NZ give-way triangle point and sign face toward approaching traffic (local +Z).</summary>
    public static Vector3 ApproachingDirection(Vector3 routeForward) => -HorizontalForward(routeForward);

    public static YieldTriangleVertices YieldTriangle(float widthMeters, float lengthMeters)
    {
        if (!float.IsFinite(widthMeters) || widthMeters <= 0) throw new ArgumentOutOfRangeException(nameof(widthMeters));
        if (!float.IsFinite(lengthMeters) || lengthMeters <= 0) throw new ArgumentOutOfRangeException(nameof(lengthMeters));
        return new YieldTriangleVertices(new Vector3(-widthMeters/2,0,-lengthMeters/2),
            new Vector3(widthMeters/2,0,-lengthMeters/2), new Vector3(0,0,lengthMeters/2));
    }

    public static Vector3 LocalToWorldDirection(Vector3 routeForward, Vector3 localDirection, float localYawRadians = 0)
    {
        var yaw = YawRadians(routeForward) + localYawRadians;
        var x = MathF.Cos(yaw) * localDirection.X + MathF.Sin(yaw) * localDirection.Z;
        var z = -MathF.Sin(yaw) * localDirection.X + MathF.Cos(yaw) * localDirection.Z;
        var result = new Vector3(x, localDirection.Y, z);
        return result.LengthSquared() < 1e-8f ? result : Vector3.Normalize(result);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
