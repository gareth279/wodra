using System;
using System.Numerics;

/// <summary>Local-offset and world-bound calculations for small instanced roadside props.</summary>
public static class RoadsidePrimitiveMath
{
    public const float MaximumStreetFurnitureExtentMeters = 8;

    public static Vector3 RotateLocalOffset(Vector3 offset, float yawRadians)
    {
        if (!Finite(offset) || !float.IsFinite(yawRadians)) throw new ArgumentOutOfRangeException(nameof(yawRadians));
        var sin = MathF.Sin(yawRadians);
        var cos = MathF.Cos(yawRadians);
        return new Vector3(cos * offset.X + sin * offset.Z, offset.Y, -sin * offset.X + cos * offset.Z);
    }

    public static Vector3 WorldAabbSize(Vector3 meshSize, Vector3 scale, float yawRadians)
    {
        if (!Finite(meshSize) || !Finite(scale) || scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0 || !float.IsFinite(yawRadians))
            throw new ArgumentOutOfRangeException(nameof(scale));
        var scaledX = Math.Abs(meshSize.X) * scale.X;
        var scaledY = Math.Abs(meshSize.Y) * scale.Y;
        var scaledZ = Math.Abs(meshSize.Z) * scale.Z;
        var sin = Math.Abs(MathF.Sin(yawRadians));
        var cos = Math.Abs(MathF.Cos(yawRadians));
        return new Vector3(cos * scaledX + sin * scaledZ, scaledY, sin * scaledX + cos * scaledZ);
    }

    public static bool IsPlausibleStreetFurnitureSize(Vector3 worldAabbSize) =>
        Finite(worldAabbSize) && worldAabbSize.X <= MaximumStreetFurnitureExtentMeters
        && worldAabbSize.Y <= MaximumStreetFurnitureExtentMeters
        && worldAabbSize.Z <= MaximumStreetFurnitureExtentMeters;

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
