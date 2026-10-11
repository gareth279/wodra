using System;
using System.Numerics;

/// <summary>Pure cadence, wheel, leg-joint, and restrained corner-lean math for the rider visual.</summary>
public static class CyclistAnimationMath
{
    public const double DefaultWheelRadiusMeters = 0.35;
    public const double MaximumLeanDegrees = 10;
    public const double GravityMetersPerSecondSquared = 9.80665;
    public const double CadenceResponseSeconds = 0.22;

    public static double SmoothCadence(double currentRpm, double targetRpm, double deltaSeconds,
        double responseSeconds = CadenceResponseSeconds)
    {
        if (!double.IsFinite(currentRpm) || !double.IsFinite(targetRpm) || !double.IsFinite(deltaSeconds)
            || !double.IsFinite(responseSeconds) || currentRpm < 0 || targetRpm < 0 || responseSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentRpm));
        if (deltaSeconds <= 0) return currentRpm;
        var weight = 1 - Math.Exp(-deltaSeconds / responseSeconds);
        var smoothed = currentRpm + (targetRpm - currentRpm) * weight;
        return smoothed < 0.02 ? 0 : smoothed;
    }

    public static double AdvanceCrankAngle(double angleRadians, double cadenceRpm, double deltaSeconds, bool paused = false)
    {
        if (paused || cadenceRpm <= 0 || deltaSeconds <= 0) return WrapAngle(angleRadians);
        return WrapAngle(angleRadians + cadenceRpm / 60d * Math.Tau * deltaSeconds);
    }

    public static double CrankAngularSpeedRadiansPerSecond(double cadenceRpm, bool paused = false) =>
        paused ? 0 : Math.Max(0, cadenceRpm) / 60d * Math.Tau;

    public static double WheelAngularSpeedRadiansPerSecond(double virtualSpeedKph,
        double wheelRadiusMeters = DefaultWheelRadiusMeters, bool paused = false)
    {
        if (paused || virtualSpeedKph <= 0) return 0;
        if (!double.IsFinite(wheelRadiusMeters) || wheelRadiusMeters <= 0) throw new ArgumentOutOfRangeException(nameof(wheelRadiusMeters));
        return virtualSpeedKph / 3.6 / wheelRadiusMeters;
    }

    /// <summary>Returns crank offset as (fore/aft Z, vertical Y); opposite pedals use angles separated by pi.</summary>
    public static Vector2 PedalOffset(double crankAngleRadians, double crankRadiusMeters)
    {
        if (!double.IsFinite(crankAngleRadians) || !double.IsFinite(crankRadiusMeters) || crankRadiusMeters < 0)
            throw new ArgumentOutOfRangeException(nameof(crankRadiusMeters));
        return new Vector2((float)(crankRadiusMeters * Math.Cos(crankAngleRadians)),
            (float)(crankRadiusMeters * Math.Sin(crankAngleRadians)));
    }

    public static double AdvanceWheelAngle(double angleRadians, double virtualSpeedKph, double deltaSeconds,
        double wheelRadiusMeters = DefaultWheelRadiusMeters, bool paused = false)
    {
        if (deltaSeconds <= 0 || paused) return WrapAngle(angleRadians);
        return WrapAngle(angleRadians + WheelAngularSpeedRadiansPerSecond(virtualSpeedKph, wheelRadiusMeters) * deltaSeconds);
    }

    /// <summary>Solves a knee in 2D where Vector2.X is fore/aft and Y is vertical.</summary>
    public static Vector2 SolveKnee(Vector2 hip, Vector2 ankle, double upperLengthMeters,
        double lowerLengthMeters, double bendDirection)
    {
        if (!Finite(hip) || !Finite(ankle) || !double.IsFinite(upperLengthMeters) || !double.IsFinite(lowerLengthMeters)
            || upperLengthMeters <= 0 || lowerLengthMeters <= 0 || !double.IsFinite(bendDirection))
            throw new ArgumentOutOfRangeException(nameof(upperLengthMeters));
        var delta = ankle - hip;
        var actualDistance = delta.Length();
        var minDistance = (float)Math.Abs(upperLengthMeters - lowerLengthMeters) + 0.0001f;
        var maxDistance = (float)(upperLengthMeters + lowerLengthMeters) - 0.0001f;
        var distance = Math.Clamp(actualDistance, minDistance, maxDistance);
        var direction = actualDistance > 0.00001f ? delta / actualDistance : Vector2.UnitX;
        var along = (float)((upperLengthMeters * upperLengthMeters + distance * distance - lowerLengthMeters * lowerLengthMeters)
            / (2 * upperLengthMeters * distance));
        var perpendicular = (float)Math.Sqrt(Math.Max(0, 1 - along * along));
        var bend = new Vector2(-direction.Y, direction.X) * (float)Math.Sign(bendDirection == 0 ? 1 : bendDirection);
        return hip + direction * (float)(upperLengthMeters * along) + bend * (float)(upperLengthMeters * perpendicular);
    }

    public static double CornerLeanDegrees(Vector3 previousForward, Vector3 currentForward,
        double deltaSeconds, double virtualSpeedKph, double maximumLeanDegrees = MaximumLeanDegrees)
    {
        if (deltaSeconds <= 0 || virtualSpeedKph <= 0) return 0;
        if (!float.IsFinite(previousForward.X + previousForward.Z + currentForward.X + currentForward.Z)
            || !double.IsFinite(maximumLeanDegrees) || maximumLeanDegrees < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumLeanDegrees));
        var previousHorizontal = new Vector3(previousForward.X, 0, previousForward.Z);
        var currentHorizontal = new Vector3(currentForward.X, 0, currentForward.Z);
        if (previousHorizontal.LengthSquared() < 1e-8f || currentHorizontal.LengthSquared() < 1e-8f) return 0;
        var previous = Vector3.Normalize(previousHorizontal);
        var current = Vector3.Normalize(currentHorizontal);
        var crossY = Vector3.Cross(previous, current).Y;
        var dot = Math.Clamp(Vector3.Dot(previous, current), -1f, 1f);
        var yawRate = Math.Atan2(crossY, dot) / deltaSeconds;
        var lateralAcceleration = virtualSpeedKph / 3.6 * yawRate;
        var lean = Math.Atan2(lateralAcceleration, GravityMetersPerSecondSquared) * 180 / Math.PI;
        return Math.Clamp(lean, -maximumLeanDegrees, maximumLeanDegrees);
    }

    public static double WrapAngle(double radians)
    {
        var value = radians % Math.Tau;
        return value < 0 ? value + Math.Tau : value;
    }

    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
