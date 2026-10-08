using System;
using System.Numerics;
using IndoorCycling.Core.Models;

public readonly record struct RiderVisualInput(Vector3 Position, Vector3 Forward,
    double SpeedKph, int CadenceRpm, double GradientPercent, bool Paused);

/// <summary>Maps source-independent rider state and the shared route pose to visual inputs.</summary>
public static class RiderVisualInputMath
{
    public static RiderVisualInput Create(RiderState rider, VisualRoutePose routePose, double gradientPercent)
    {
        var position = RiderLaneMath.OffsetPosition(routePose.Position, routePose.Forward,
            (float)rider.LateralOffsetMeters) + Vector3.UnitY * 0.04f;
        return new RiderVisualInput(position, routePose.Forward, Math.Max(0, rider.VirtualSpeedMps) * 3.6,
            Math.Max(0, rider.CadenceRpm), gradientPercent, rider.Paused);
    }
}
