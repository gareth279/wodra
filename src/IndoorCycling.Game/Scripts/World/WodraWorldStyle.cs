using System;
using Godot;

/// <summary>Shared art direction values for Wodra's route-local world rendering.</summary>
public static class WodraWorldStyle
{
    public static readonly Color Sunlight = new(1.0f, 0.82f, 0.62f);
    public static readonly Color SkyTop = new(0.18f, 0.32f, 0.55f);
    public static readonly Color SkyHorizon = new(0.78f, 0.62f, 0.48f);
    public static readonly Color GroundHorizon = new(0.54f, 0.47f, 0.34f);
    public static readonly Color Grass = new(0.34f, 0.43f, 0.24f);
    public static readonly Color Meadow = new(0.52f, 0.51f, 0.29f);
    public static readonly Color Forest = new(0.20f, 0.34f, 0.21f);
    public static readonly Color Rock = new(0.48f, 0.43f, 0.34f);
    public static readonly Color Asphalt = new(0.16f, 0.17f, 0.19f);

    public const float SunEnergy = 1.18f;
    public const float AmbientEnergy = 0.62f;
    public const float FogDensity = 0.0018f;
    public const float FogAerialPerspective = 0.18f;
    public const float FogSkyAffect = 0.22f;
    public const int ShadowDistanceMeters = 220;
    public const float VegetationVisibleDistanceMeters = 1000;
    public const int VegetationChunkLengthMeters = 400;

    public static Color TerrainFaceColor(float heightMeters, float upwardness, float variation)
    {
        var color = heightMeters switch
        {
            < 18 => Grass,
            < 80 => Grass.Lerp(Meadow, Math.Clamp((heightMeters - 18) / 62f, 0, 0.52f)),
            _ => Meadow.Lerp(Rock, Math.Clamp((heightMeters - 80) / 180f, 0, 0.68f))
        };
        if (upwardness < 0.78f) color = color.Lerp(Rock, Math.Clamp((0.78f - upwardness) * 1.8f, 0, 0.58f));
        return color.Lerp(upwardness < 0.78f ? Rock : Meadow, variation * 0.12f);
    }
}
