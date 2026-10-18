using System;
using System.Numerics;

/// <summary>Pure camera boom clipping helpers. Distances are route-world metres.</summary>
public static class CameraObstructionMath
{
    public static Vector3 ClampToHit(Vector3 anchor, Vector3 target, Vector3? hitPosition,
        float clearanceMeters = 0.3f)
    {
        if (!float.IsFinite(clearanceMeters) || clearanceMeters < 0)
            throw new ArgumentOutOfRangeException(nameof(clearanceMeters));
        var offset = target - anchor;
        var length = offset.Length();
        if (length <= 1e-5f || hitPosition is not { } hit) return target;
        var direction = offset / length;
        var hitDistance = Math.Clamp(Vector3.Dot(hit - anchor, direction), 0, length);
        return anchor + direction * Math.Max(0, hitDistance - clearanceMeters);
    }

    /// <summary>Retract immediately for safety; ease the boom back out after the path clears.</summary>
    public static float ResolveBoomDistance(float currentMeters, float allowedMeters,
        double deltaSeconds, float recoveryRate = 3.5f)
    {
        if (!float.IsFinite(currentMeters) || !float.IsFinite(allowedMeters)
            || currentMeters < 0 || allowedMeters < 0
            || !double.IsFinite(deltaSeconds) || deltaSeconds < 0
            || !float.IsFinite(recoveryRate) || recoveryRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(allowedMeters));
        if (allowedMeters <= currentMeters) return allowedMeters;
        var weight = (float)(1 - Math.Exp(-recoveryRate * deltaSeconds));
        return currentMeters + (allowedMeters - currentMeters) * weight;
    }
}
