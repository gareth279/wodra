using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

internal readonly record struct LocalPlayerCarConstraint(double AllowedRouteDistanceMeters,
    int? BlockingVehicleId, float MinimumClearanceMeters, bool FollowOnly);

/// <summary>Local-player-only swept clearance against production ambient-car model envelopes.</summary>
internal static class LocalPlayerCarInteractionMath
{
    public const float MinimumPassingRoadWidthMeters = 6.5f;
    public const float MinimumPassingClearanceMeters = .45f;
    public const float MinimumFollowingGapMeters = 1.2f;
    private const float MaximumSweepStepMeters = .08f;
    private const int MaximumSweepSamples = 512;

    public static LocalPlayerCarConstraint ClampRouteProgress(double startDistanceMeters,
        double requestedDistanceMeters, double deltaSeconds, float lateralOffsetMeters,
        float riderLengthMeters, float riderWidthMeters, float riderHeightMeters,
        Func<double, float, AmbientTrafficPose> riderPoseAt,
        IReadOnlyList<AmbientCarInteractionSample> cars)
    {
        var requestedProgress = requestedDistanceMeters - startDistanceMeters;
        if (!double.IsFinite(startDistanceMeters) || !double.IsFinite(requestedDistanceMeters)
            || requestedProgress <= 1e-8 || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0
            || cars.Count == 0)
            return new LocalPlayerCarConstraint(requestedDistanceMeters, null, float.PositiveInfinity, false);

        var startPose = riderPoseAt(startDistanceMeters, lateralOffsetMeters);
        var maxCarSpeed = cars.Max(car => Math.Max(0, car.Motion.SpeedMetersPerSecond));
        var relevantCars = cars.Where(car => Vector3.Distance(car.Motion.Position, startPose.Position)
                <= Math.Max(80, requestedProgress + maxCarSpeed * (float)deltaSeconds + 20))
            .ToArray();
        if (relevantCars.Length == 0)
            return new LocalPlayerCarConstraint(requestedDistanceMeters, null, float.PositiveInfinity, false);

        var fullCheck = CheckProgress(requestedProgress);
        if (fullCheck.Safe)
            return new LocalPlayerCarConstraint(requestedDistanceMeters, null, fullCheck.MinimumClearance, false);

        var low = 0d;
        var high = requestedProgress;
        var closest = fullCheck;
        for (var iteration = 0; iteration < 22; iteration++)
        {
            var middle = (low + high) * .5;
            var check = CheckProgress(middle);
            if (check.Safe) low = middle;
            else { high = middle; closest = check; }
        }

        return new LocalPlayerCarConstraint(startDistanceMeters + low, closest.VehicleId,
            closest.MinimumClearance, closest.FollowOnly);

        (bool Safe, int? VehicleId, float MinimumClearance, bool FollowOnly) CheckProgress(double progress)
        {
            var maxRelativeTravel = progress + maxCarSpeed * deltaSeconds;
            var samples = Math.Clamp((int)Math.Ceiling(maxRelativeTravel / MaximumSweepStepMeters), 1, MaximumSweepSamples);
            var minimumClearance = float.PositiveInfinity;
            int? blockingVehicle = null;
            var followOnly = false;
            for (var sample = 1; sample <= samples; sample++)
            {
                var t = deltaSeconds * sample / samples;
                var playerDistance = startDistanceMeters + progress * sample / samples;
                var playerPose = riderPoseAt(playerDistance, lateralOffsetMeters);
                foreach (var car in relevantCars)
                {
                    var carPose = car.Motion.PoseAt(t);
                    var clearance = AmbientActorEnvelopeMath.SignedClearance(playerPose.Position,
                        playerPose.Forward, riderLengthMeters, riderWidthMeters, riderHeightMeters,
                        carPose, AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
                        AmbientActorEnvelopeMath.CarHeightMeters);
                    minimumClearance = Math.Min(minimumClearance, clearance);
                    var sameDirection = Vector3.Dot(Horizontal(playerPose.Forward), Horizontal(carPose.Forward)) > .82f;
                    var relative = carPose.Position - playerPose.Position;
                    var aheadGap = Vector3.Dot(relative, Horizontal(carPose.Forward));
                    var lateralGap = Math.Abs(Vector3.Dot(relative, new Vector3(-carPose.Forward.Z, 0, carPose.Forward.X)));
                    var verticalOverlap = playerPose.Position.Y < carPose.Position.Y + AmbientActorEnvelopeMath.CarHeightMeters
                        && carPose.Position.Y < playerPose.Position.Y + riderHeightMeters;
                    var narrowRoadFollow = car.RoadWidthMeters < MinimumPassingRoadWidthMeters
                        && verticalOverlap
                        && sameDirection && aheadGap > 0
                        && lateralGap < AmbientActorEnvelopeMath.CarWidthMeters * .5f + riderWidthMeters * .5f + 2.0f
                        && aheadGap <= AmbientActorEnvelopeMath.CarLengthMeters * .5f
                            + riderLengthMeters * .5f + MinimumFollowingGapMeters;
                    if (clearance < MinimumPassingClearanceMeters || narrowRoadFollow)
                    {
                        blockingVehicle = car.VehicleId;
                        followOnly = narrowRoadFollow && clearance >= MinimumPassingClearanceMeters;
                        return (false, blockingVehicle, minimumClearance, followOnly);
                    }
                }
            }
            return (true, null, minimumClearance, false);
        }
    }

    public static (double AllowedOffsetMeters, int? BlockingVehicleId, float MinimumClearanceMeters)
        ClampLateralOffset(double routeDistanceMeters, double startOffsetMeters, double requestedOffsetMeters,
            double deltaSeconds, float riderLengthMeters, float riderWidthMeters, float riderHeightMeters,
            Func<double, float, AmbientTrafficPose> riderPoseAt,
            IReadOnlyList<AmbientCarInteractionSample> cars)
    {
        if (cars.Count == 0 || Math.Abs(requestedOffsetMeters - startOffsetMeters) <= 1e-8
            || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0)
            return (requestedOffsetMeters, null, float.PositiveInfinity);

        var startPose = riderPoseAt(routeDistanceMeters, (float)startOffsetMeters);
        var relevantCars = cars.Where(car => Vector3.Distance(car.Motion.Position, startPose.Position) < 25).ToArray();
        if (relevantCars.Length == 0) return (requestedOffsetMeters, null, float.PositiveInfinity);
        var desired = requestedOffsetMeters - startOffsetMeters;
        var full = CheckOffset(desired);
        if (full.Safe) return (requestedOffsetMeters, null, full.Clearance);

        var low = 0d;
        var high = 1d;
        var blocked = full;
        for (var iteration = 0; iteration < 22; iteration++)
        {
            var middle = (low + high) * .5;
            var check = CheckOffset(desired * middle);
            if (check.Safe) low = middle;
            else { high = middle; blocked = check; }
        }
        return (startOffsetMeters + desired * low, blocked.VehicleId, blocked.Clearance);

        (bool Safe, int? VehicleId, float Clearance) CheckOffset(double offsetDelta)
        {
            var lateralTravel = Math.Abs(offsetDelta);
            var samples = Math.Clamp((int)Math.Ceiling(lateralTravel / MaximumSweepStepMeters), 1, 128);
            var minimum = float.PositiveInfinity;
            for (var sample = 1; sample <= samples; sample++)
            {
                var t = deltaSeconds * sample / samples;
                var offset = (float)(startOffsetMeters + offsetDelta * sample / samples);
                var playerPose = riderPoseAt(routeDistanceMeters, offset);
                foreach (var car in relevantCars)
                {
                    var carPose = car.Motion.PoseAt(t);
                    var clearance = AmbientActorEnvelopeMath.SignedClearance(playerPose.Position,
                        playerPose.Forward, riderLengthMeters, riderWidthMeters, riderHeightMeters,
                        carPose, AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
                        AmbientActorEnvelopeMath.CarHeightMeters);
                    minimum = Math.Min(minimum, clearance);
                    // Steering can move a close rider away from an existing gap, but may not
                    // put the rendered rider or bicycle into the car's actual model envelope.
                    if (clearance < 0) return (false, car.VehicleId, minimum);
                }
            }
            return (true, null, minimum);
        }
    }

    private static Vector3 Horizontal(Vector3 value)
    {
        value.Y = 0;
        return value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : Vector3.Zero;
    }
}
