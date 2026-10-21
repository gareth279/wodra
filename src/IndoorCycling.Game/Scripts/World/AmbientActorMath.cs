using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization;

public enum AmbientActorKind { Car, Pedestrian }

/// <summary>Transient local scenery state. ActorId is session-local and has no persistence or network meaning.</summary>
public readonly record struct AmbientActorState(int ActorId, AmbientActorKind Kind, Vector3 WorldPosition,
    Vector3 Heading, float SpeedMetersPerSecond, bool Active);

/// <summary>JSON-safe coordinates for ambient actor diagnostics (System.Numerics.Vector3 serializes as an empty object by default).</summary>
public readonly record struct AmbientActorDiagnosticPoint(
    [property: JsonPropertyName("x")] float X,
    [property: JsonPropertyName("y")] float Y,
    [property: JsonPropertyName("z")] float Z)
{
    public static AmbientActorDiagnosticPoint From(Vector3 position) => new(position.X, position.Y, position.Z);
}

public readonly record struct AmbientRouteSample(double DistanceMeters, Vector3 Position, Vector3 Forward);
public sealed record AmbientRoadInput(long OsmId, string Highway, bool Oneway, double WidthMeters,
    IReadOnlyList<Vector3> Points, string? Name = null,
    IReadOnlyList<AmbientRoadRouteConnection>? RouteConnections = null, int OnewayDirection = 1);
public readonly record struct AmbientRoadRouteConnection(int EndIndex, double RouteDistanceMeters, Vector3 RoutePosition);
public readonly record struct AmbientSpeedRange(float MinimumMetersPerSecond, float MaximumMetersPerSecond);
public readonly record struct AmbientSpawnSelection(int PathIndex, double DistanceMeters, Vector3 Position);
public readonly record struct AmbientTrafficPose(double TimeSeconds, Vector3 Position, Vector3 Forward);
public readonly record struct AmbientTrafficRider(string RiderId, Vector3 Position, Vector3 Forward,
    float SpeedMetersPerSecond, float LengthMeters = 1.9f, float WidthMeters = .86f,
    float HeightMeters = 1.8f, IReadOnlyList<AmbientTrafficPose>? Prediction = null)
{
    public AmbientTrafficPose PoseAt(double seconds)
    {
        if (Prediction is not { Count: > 0 } samples)
            return new AmbientTrafficPose(seconds, Position + HorizontalUnit(Forward) *
                (Math.Max(0, SpeedMetersPerSecond) * (float)seconds), Forward);
        if (seconds <= samples[0].TimeSeconds) return samples[0];
        // Traffic predictions are queried for each vehicle/obstacle pair at many
        // look-ahead samples. Binary search keeps this bounded as population grows.
        var low = 0;
        var high = samples.Count - 1;
        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            if (seconds <= samples[middle].TimeSeconds) high = middle;
            else low = middle;
        }
        if (seconds <= samples[^1].TimeSeconds)
        {
            var before = samples[low]; var after = samples[high];
            var span = after.TimeSeconds - before.TimeSeconds;
            var t = span <= 1e-8 ? 0 : (float)Math.Clamp((seconds - before.TimeSeconds) / span, 0, 1);
            var heading = Vector3.Lerp(before.Forward, after.Forward, t);
            if (heading.LengthSquared() > 1e-8f) heading = Vector3.Normalize(heading);
            return new AmbientTrafficPose(seconds, Vector3.Lerp(before.Position, after.Position, t), heading);
        }
        var last = samples[^1];
        var extra = Math.Max(0, seconds - last.TimeSeconds);
        return new AmbientTrafficPose(seconds, last.Position + HorizontalUnit(last.Forward) *
            (Math.Max(0, SpeedMetersPerSecond) * (float)extra), last.Forward);
    }

    private static Vector3 HorizontalUnit(Vector3 value)
    {
        value.Y = 0;
        return value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : Vector3.UnitX;
    }
}

/// <summary>3D world-envelope checks for moving ambient actors; route trajectories preserve bends and grades.</summary>
public static class AmbientActorEnvelopeMath
{
    public const float SafetyMarginMeters = .18f;
    public const float CarLengthMeters = 4.45f;
    public const float CarWidthMeters = 1.82f;
    public const float CarHeightMeters = 1.63f;
    public const float PedestrianLengthMeters = .55f;
    public const float PedestrianWidthMeters = .45f;
    public const float PedestrianHeightMeters = 1.8f;

    public static bool Overlaps(Vector3 firstBase, Vector3 firstForward, float firstLength,
        float firstWidth, float firstHeight, AmbientTrafficPose secondPose, float secondLength,
        float secondWidth, float secondHeight, float safetyMargin = SafetyMarginMeters) =>
        SignedClearance(firstBase, firstForward, firstLength, firstWidth, firstHeight,
            secondPose, secondLength, secondWidth, secondHeight) <= safetyMargin;

    /// <summary>Positive means separated, zero touches, negative means model-envelope penetration.</summary>
    public static float SignedClearance(Vector3 firstBase, Vector3 firstForward, float firstLength,
        float firstWidth, float firstHeight, AmbientTrafficPose secondPose, float secondLength,
        float secondWidth, float secondHeight)
    {
        if (!Finite(firstBase) || !Finite(firstForward) || !Finite(secondPose.Position)
            || !Finite(secondPose.Forward) || !float.IsFinite(firstLength) || !float.IsFinite(secondLength)
            || !float.IsFinite(firstWidth) || !float.IsFinite(secondWidth)
            || !float.IsFinite(firstHeight) || !float.IsFinite(secondHeight)) return float.PositiveInfinity;
        var firstTop = firstBase.Y + firstHeight;
        var secondTop = secondPose.Position.Y + secondHeight;
        var verticalSeparation = Math.Max(secondPose.Position.Y - firstTop, firstBase.Y - secondTop);
        var aForward = HorizontalUnit(firstForward);
        var bForward = HorizontalUnit(secondPose.Forward);
        if (aForward == Vector3.Zero) return float.PositiveInfinity;
        // A stationary pedestrian has no movement heading; treat its envelope as aligned to
        // the approaching vehicle so that its footprint is still checked conservatively.
        if (bForward == Vector3.Zero) bForward = aForward;
        var aSide = new Vector3(-aForward.Z, 0, aForward.X);
        var bSide = new Vector3(-bForward.Z, 0, bForward.X);
        var relative = new Vector3(secondPose.Position.X - firstBase.X, 0, secondPose.Position.Z - firstBase.Z);
        var planarSeparation = Math.Max(SeparationOnAxis(relative, aForward, firstLength * .5f,
                firstWidth * .5f, secondLength * .5f, secondWidth * .5f, aForward, bForward, aSide, bSide),
            Math.Max(SeparationOnAxis(relative, aSide, firstLength * .5f,
                    firstWidth * .5f, secondLength * .5f, secondWidth * .5f, aForward, bForward, aSide, bSide),
                Math.Max(SeparationOnAxis(relative, bForward, firstLength * .5f,
                        firstWidth * .5f, secondLength * .5f, secondWidth * .5f, aForward, bForward, aSide, bSide),
                    SeparationOnAxis(relative, bSide, firstLength * .5f,
                        firstWidth * .5f, secondLength * .5f, secondWidth * .5f, aForward, bForward, aSide, bSide))));
        return Math.Max(verticalSeparation, planarSeparation);
    }

    private static float SeparationOnAxis(Vector3 relative, Vector3 axis,
        float aHalfLength, float aHalfWidth, float bHalfLength, float bHalfWidth,
        Vector3 aForward, Vector3 bForward, Vector3 aSide, Vector3 bSide)
    {
        var radiusA = aHalfLength * Math.Abs(Vector3.Dot(aForward, axis))
            + aHalfWidth * Math.Abs(Vector3.Dot(aSide, axis));
        var radiusB = bHalfLength * Math.Abs(Vector3.Dot(bForward, axis))
            + bHalfWidth * Math.Abs(Vector3.Dot(bSide, axis));
        return Math.Abs(Vector3.Dot(relative, axis)) - radiusA - radiusB;
    }

    private static Vector3 HorizontalUnit(Vector3 value)
    {
        value.Y = 0;
        return value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : Vector3.Zero;
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>Speed control for scenery cars sharing a lane with riders; it never changes rider/ride state.</summary>
public static class AmbientTrafficFollowingMath
{
    // Clearance is measured between model envelopes. This remains an extra surface gap,
    // rather than a center-to-center threshold that ignores vehicle and bicycle length.
    public const float MinimumFollowingGapMeters = 1.2f;
    public const float FollowingHeadwaySeconds = 1.5f;
    public const float MaximumBrakingMetersPerSecondSquared = 5f;
    public const float MaximumLateralMatchMeters = 1.55f;
    public const float MaximumFollowingLookAheadMeters = 55f;
    public const float MaximumAccelerationMetersPerSecondSquared = 2.5f;
    public const float MaximumConflictLookAheadSeconds = 8f;
    public const float ConflictSampleSeconds = .1f;
    public const float PredictionHorizonSeconds = 12f;
    public const float PredictionSampleSeconds = .25f;

    public static float TargetSpeed(Vector3 vehiclePosition, Vector3 vehicleForward, float cruiseSpeedMetersPerSecond,
        IReadOnlyList<AmbientTrafficRider> riders)
    {
        if (!Finite(vehiclePosition) || !Finite(vehicleForward) || vehicleForward.LengthSquared() < 1e-8f
            || !float.IsFinite(cruiseSpeedMetersPerSecond) || cruiseSpeedMetersPerSecond <= 0)
            return 0;
        var forward = HorizontalUnit(vehicleForward);
        if (forward.LengthSquared() < 1e-8f) return cruiseSpeedMetersPerSecond;
        var target = cruiseSpeedMetersPerSecond;
        foreach (var rider in riders)
        {
            if (!Finite(rider.Position) || !Finite(rider.Forward) || !float.IsFinite(rider.SpeedMetersPerSecond)) continue;
            var riderForward = HorizontalUnit(rider.Forward);
            if (riderForward.LengthSquared() < 1e-8f || Vector3.Dot(forward, riderForward) < .45f) continue;
            var relative = new Vector3(rider.Position.X - vehiclePosition.X, 0, rider.Position.Z - vehiclePosition.Z);
            var longitudinal = Vector3.Dot(relative, forward);
            if (longitudinal <= 0 || longitudinal > MaximumFollowingLookAheadMeters) continue;
            var side = new Vector3(-forward.Z, 0, forward.X);
            var lateral = Math.Abs(Vector3.Dot(relative, side));
            var lateralReach = AmbientActorEnvelopeMath.CarWidthMeters * .5f + rider.WidthMeters * .5f
                + AmbientActorEnvelopeMath.SafetyMarginMeters;
            if (lateral > Math.Max(MaximumLateralMatchMeters, lateralReach)
                || Math.Abs(rider.Position.Y - vehiclePosition.Y) > rider.HeightMeters + .9f) continue;

            var gap = Math.Max(0, longitudinal - AmbientActorEnvelopeMath.CarLengthMeters * .5f
                - rider.LengthMeters * .5f - MinimumFollowingGapMeters);
            var riderSpeed = Math.Max(0, rider.SpeedMetersPerSecond);
            var headwaySpeed = riderSpeed + gap / FollowingHeadwaySeconds;
            var brakingSpeed = MathF.Sqrt(riderSpeed * riderSpeed
                + 2 * MaximumBrakingMetersPerSecondSquared * gap);
            target = Math.Min(target, Math.Min(headwaySpeed, brakingSpeed));
        }
        return Math.Max(0, target);
    }

    /// <summary>Predicts the vehicle and each obstacle along their actual curved paths.</summary>
    public static float TargetSpeed(AmbientActorPath path, double distanceAlongPathMeters,
        float laneOffsetMeters, float cruiseSpeedMetersPerSecond, IReadOnlyList<AmbientTrafficRider> obstacles)
    {
        if (!path.IsUsable || !double.IsFinite(distanceAlongPathMeters)
            || !float.IsFinite(cruiseSpeedMetersPerSecond) || cruiseSpeedMetersPerSecond <= 0) return 0;
        var (position, heading) = path.SampleLane(distanceAlongPathMeters, laneOffsetMeters);
        var target = TargetSpeed(position, heading, cruiseSpeedMetersPerSecond, obstacles);
        var samples = Math.Max(1, (int)Math.Ceiling(MaximumConflictLookAheadSeconds / ConflictSampleSeconds));
        for (var sample = 1; sample <= samples; sample++)
        {
            var seconds = sample * ConflictSampleSeconds;
            var pathDistance = AmbientActorMath.AdvancePathDistance(path, distanceAlongPathMeters,
                cruiseSpeedMetersPerSecond, seconds, paused: false);
            var (carPosition, carHeading) = path.SampleLane(pathDistance, laneOffsetMeters);
            if (!AmbientActorMath.IsValidTransform(carPosition, carHeading)) continue;
            foreach (var obstacle in obstacles)
            {
                var obstaclePose = obstacle.PoseAt(seconds);
                if (!AmbientActorEnvelopeMath.Overlaps(carPosition, carHeading,
                        AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
                        AmbientActorEnvelopeMath.CarHeightMeters, obstaclePose, obstacle.LengthMeters,
                        obstacle.WidthMeters, obstacle.HeightMeters)) continue;

                var conflictDistance = Math.Max(0, (float)(pathDistance - distanceAlongPathMeters)
                    - AmbientActorEnvelopeMath.CarLengthMeters * .5f - AmbientActorEnvelopeMath.SafetyMarginMeters);
                if (Vector3.Dot(HorizontalUnit(carHeading), HorizontalUnit(obstaclePose.Forward)) > .55f)
                {
                    var freeGap = Math.Max(0, conflictDistance - obstacle.LengthMeters * .5f - MinimumFollowingGapMeters);
                    var followingCap = obstacle.SpeedMetersPerSecond + freeGap / FollowingHeadwaySeconds;
                    var brakingCap = MathF.Sqrt(obstacle.SpeedMetersPerSecond * obstacle.SpeedMetersPerSecond
                        + 2 * MaximumBrakingMetersPerSecondSquared * freeGap);
                    target = Math.Min(target, Math.Min(followingCap, brakingCap));
                }
                else
                {
                    // Crossing paths, turnarounds and opposing traffic yield before the first
                    // occupied swept envelope instead of assuming a straight-line pass is clear.
                    target = Math.Min(target, MathF.Sqrt(2 * MaximumBrakingMetersPerSecondSquared * conflictDistance));
                }
                break;
            }
        }
        return Math.Max(0, target);
    }

    public static float ApproachSpeed(float currentSpeedMetersPerSecond, float targetSpeedMetersPerSecond, double deltaSeconds)
    {
        if (!float.IsFinite(currentSpeedMetersPerSecond) || !float.IsFinite(targetSpeedMetersPerSecond)
            || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0) return Math.Max(0, currentSpeedMetersPerSecond);
        var acceleration = targetSpeedMetersPerSecond < currentSpeedMetersPerSecond
            ? MaximumBrakingMetersPerSecondSquared : MaximumAccelerationMetersPerSecondSquared;
        var step = (float)(acceleration * deltaSeconds);
        return targetSpeedMetersPerSecond < currentSpeedMetersPerSecond
            ? Math.Max(targetSpeedMetersPerSecond, currentSpeedMetersPerSecond - step)
            : Math.Min(targetSpeedMetersPerSecond, currentSpeedMetersPerSecond + step);
    }

    /// <summary>Caps one update at the last sample that preserves swept 3D model-envelope clearance.</summary>
    public static float ClampSpeedForClearance(AmbientActorPath path, double pathDistanceMeters,
        float proposedSpeedMetersPerSecond, double deltaSeconds, IReadOnlyList<AmbientTrafficRider> cyclists,
        IReadOnlyList<AmbientTrafficRider> pedestrians, IReadOnlyList<AmbientTrafficRider>? otherCars = null,
        float laneOffsetMeters = 0)
    {
        if (!path.IsUsable || !double.IsFinite(pathDistanceMeters) || !float.IsFinite(proposedSpeedMetersPerSecond)
            || proposedSpeedMetersPerSecond <= 0 || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0) return 0;
        var travel = proposedSpeedMetersPerSecond * deltaSeconds;
        var samples = Math.Clamp((int)Math.Ceiling(travel / .08), 1, 256);
        var safeProgress = 0d;
        var obstacles = cyclists.Concat(pedestrians).Concat(otherCars ?? Array.Empty<AmbientTrafficRider>()).ToArray();
        for (var sample = 1; sample <= samples; sample++)
        {
            var time = deltaSeconds * sample / samples;
            var pathDistance = AmbientActorMath.AdvancePathDistance(path, pathDistanceMeters,
                proposedSpeedMetersPerSecond, time, paused: false);
            var (vehiclePosition, vehicleHeading) = path.SampleLane(pathDistance, laneOffsetMeters);
            var blocked = obstacles.Any(obstacle => AmbientActorEnvelopeMath.Overlaps(
                vehiclePosition, vehicleHeading, AmbientActorEnvelopeMath.CarLengthMeters,
                AmbientActorEnvelopeMath.CarWidthMeters, AmbientActorEnvelopeMath.CarHeightMeters,
                obstacle.PoseAt(time), obstacle.LengthMeters, obstacle.WidthMeters, obstacle.HeightMeters));
            if (blocked) return (float)(safeProgress / deltaSeconds);
            safeProgress = travel * sample / samples;
        }
        return proposedSpeedMetersPerSecond;
    }

    private static Vector3 HorizontalUnit(Vector3 value)
    {
        value.Y = 0;
        return value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : Vector3.Zero;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>Immutable, cumulative-length path used by a lightweight ambient actor.</summary>
public sealed class AmbientActorPath
{
    private readonly Vector3[] _points;
    private readonly double[] _cumulative;
    public AmbientActorKind Kind { get; }
    public long SourceId { get; }
    public string? SourceName { get; }
    public string? SourceClass { get; }
    public int Direction { get; }
    public AmbientSpeedRange SpeedRange { get; }
    public IReadOnlyList<Vector3> Points => _points;
    public double LengthMeters => _cumulative.Length == 0 ? 0 : _cumulative[^1];
    public double? LoopStartDistanceMeters { get; }
    public bool ContinuesAroundRoute => LoopStartDistanceMeters.HasValue;
    public double? RouteConnectionDistanceMeters { get; }
    public double RoadWidthMeters { get; }
    public float BaseLaneOffsetMeters { get; }

    public AmbientActorPath(AmbientActorKind kind, long sourceId, int direction,
        IEnumerable<Vector3> points, AmbientSpeedRange speedRange, string? sourceName = null, string? sourceClass = null,
        double? loopStartDistanceMeters = null, double? routeConnectionDistanceMeters = null,
        double roadWidthMeters = 7, float baseLaneOffsetMeters = 0)
    {
        Kind = kind; SourceId = sourceId; Direction = direction; SpeedRange = speedRange;
        SourceName = sourceName; SourceClass = sourceClass;
        _points = points.Where(Finite).Aggregate(new List<Vector3>(), (list, p) =>
        {
            if (list.Count == 0 || Vector3.DistanceSquared(list[^1], p) > 1e-8f) list.Add(p);
            return list;
        }).ToArray();
        _cumulative = new double[_points.Length];
        for (var i = 1; i < _points.Length; i++)
            _cumulative[i] = _cumulative[i - 1] + Vector3.Distance(_points[i - 1], _points[i]);
        LoopStartDistanceMeters = loopStartDistanceMeters is { } loopStart && double.IsFinite(loopStart)
            && loopStart >= 0 && loopStart < LengthMeters ? loopStart : null;
        RouteConnectionDistanceMeters = routeConnectionDistanceMeters;
        RoadWidthMeters = double.IsFinite(roadWidthMeters) && roadWidthMeters > 0 ? roadWidthMeters : 0;
        BaseLaneOffsetMeters = float.IsFinite(baseLaneOffsetMeters) ? baseLaneOffsetMeters : 0;
    }

    public bool IsUsable => _points.Length >= 2 && double.IsFinite(LengthMeters) && LengthMeters > 0.01;

    public (Vector3 Position, Vector3 Heading) Sample(double distanceMeters)
    {
        if (!IsUsable) return (_points.FirstOrDefault(), Vector3.Zero);
        var distance = Math.Clamp(double.IsFinite(distanceMeters) ? distanceMeters : 0, 0, LengthMeters);
        var low = 0; var high = _points.Length - 1;
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (_cumulative[middle] <= distance) low = middle; else high = middle;
        }
        var segmentLength = _cumulative[high] - _cumulative[low];
        var t = segmentLength > 0 ? (float)((distance - _cumulative[low]) / segmentLength) : 0;
        var position = Vector3.Lerp(_points[low], _points[high], t);
        var heading = _points[high] - _points[low];
        return (position, heading.LengthSquared() > 1e-10f ? Vector3.Normalize(heading) : Vector3.Zero);
    }

    public (Vector3 Position, Vector3 Heading) SampleLane(double distanceMeters, float laneOffsetMeters)
    {
        var (position, heading) = Sample(distanceMeters);
        var left = Vector3.Cross(Vector3.UnitY, heading);
        if (left.LengthSquared() > 1e-8f)
            position += Vector3.Normalize(left) * (laneOffsetMeters - BaseLaneOffsetMeters);
        return (position, heading);
    }

    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}

/// <summary>Pure path construction and motion rules for ephemeral ambient actors.</summary>
public static class AmbientActorMath
{
    public const float CarHalfWidthMeters = 0.9f;
    public const float RoadEdgeClearanceMeters = 0.12f;
    public const float MaximumLaneOffsetMeters = 1.7f;
    public const float PedestrianFootpathOffsetMeters = 4.865f;
    public const float PedestrianFootpathHeightMeters = 0.125f;
    public const double DefaultSeamExclusionMeters = 6;
    public const float PedestrianMinimumSpeedMetersPerSecond = 1.1f;
    public const float PedestrianMaximumSpeedMetersPerSecond = 1.7f;

    private static readonly HashSet<string> EligibleRoads = new(StringComparer.OrdinalIgnoreCase)
        { "trunk", "primary", "secondary", "tertiary", "residential", "unclassified" };

    public static bool IsEligibleRoad(string highway) => EligibleRoads.Contains(highway ?? string.Empty);

    public static AmbientSpeedRange CarSpeedRange(string highway) => highway.ToLowerInvariant() switch
    {
        "trunk" => Kph(40, 60), "primary" => Kph(40, 55), "secondary" => Kph(35, 50),
        "tertiary" => Kph(30, 45), "residential" => Kph(25, 40), "unclassified" => Kph(25, 40),
        _ => Kph(25, 40)
    };

    public static float LeftLaneOffset(double roadWidthMeters)
    {
        if (!double.IsFinite(roadWidthMeters) || roadWidthMeters <= 0) return 0;
        return Math.Clamp((float)(roadWidthMeters * 0.5 - CarHalfWidthMeters - RoadEdgeClearanceMeters),
            0, MaximumLaneOffsetMeters);
    }

    public static IReadOnlyList<AmbientActorPath> BuildCarPaths(IEnumerable<AmbientRoadInput> streets,
        IReadOnlyList<AmbientRouteSample>? routeSamples = null, double routeLengthMeters = 0)
    {
        var paths = new List<AmbientActorPath>();
        foreach (var street in streets)
        {
            if (!IsEligibleRoad(street.Highway) || street.Points.Count < 2) continue;
            var offset = LeftLaneOffset(street.WidthMeters);
            if (!street.Oneway || street.OnewayDirection >= 0)
                Add(AmbientActorPathDirection.Forward, street.Points.ToArray());
            if (!street.Oneway || street.OnewayDirection < 0)
                Add(AmbientActorPathDirection.Reverse, street.Points.Reverse().ToArray());

            void Add(int direction, IReadOnlyList<Vector3> source)
            {
                var points = OffsetTravelPath(source, offset).ToList();
                double? loopStart = null;
                double? routeDistance = null;
                var sourceEndIndex = direction == AmbientActorPathDirection.Forward ? street.Points.Count - 1 : 0;
                var routeConnection = street.RouteConnections?.FirstOrDefault(connection => connection.EndIndex == sourceEndIndex);
                var hasRouteConnection = street.RouteConnections?.Any(connection => connection.EndIndex == sourceEndIndex) == true;
                if (hasRouteConnection && routeSamples is { Count: >= 2 } && routeLengthMeters > 0
                    && double.IsFinite(routeLengthMeters))
                {
                    var connectedJunction = routeConnection!.Value;
                    var routeArc = BuildRouteArc(routeSamples, connectedJunction.RouteDistanceMeters,
                        routeLengthMeters, Math.Min(offset, MaximumLaneOffsetMeters));
                    if (routeArc.Count >= 3)
                    {
                        var branchEndpoint = points[^1];
                        var routeEntry = routeArc[0];
                        var incoming = (points[^1] - points[^2]).LengthSquared() > 1e-8f
                            ? Vector3.Normalize(points[^1] - points[^2]) : Vector3.UnitX;
                        var outgoing = Vector3.Normalize(routeArc[1] - routeArc[0]);
                        points.AddRange(BuildTurnConnector(branchEndpoint, routeEntry, incoming, outgoing));
                        loopStart = PolylineLength(points);
                        routeDistance = connectedJunction.RouteDistanceMeters;
                        points.AddRange(routeArc.Skip(1));
                    }
                }
                var path = new AmbientActorPath(AmbientActorKind.Car, street.OsmId, direction,
                    points, CarSpeedRange(street.Highway), street.Name, street.Highway, loopStart, routeDistance,
                    street.WidthMeters, offset);
                if (path.IsUsable) paths.Add(path);
            }
        }
        return paths;
    }

    private static List<Vector3> BuildRouteArc(IReadOnlyList<AmbientRouteSample> samples, double startDistance,
        double routeLength, float lateralOffset)
    {
        var start = SampleRoute(samples, startDistance, routeLength);
        var result = new List<Vector3> { LanePosition(start.Position, start.Forward, lateralOffset) };
        for (var lap = 0; lap <= 1; lap++)
        foreach (var sample in samples)
        {
            var distance = sample.DistanceMeters + lap * routeLength;
            if (distance <= start.DistanceMeters + .001 || distance >= start.DistanceMeters + routeLength - .001) continue;
            result.Add(LanePosition(sample.Position, sample.Forward, lateralOffset));
        }
        result.Add(result[0]);
        return result;
    }

    private static IReadOnlyList<Vector3> BuildTurnConnector(Vector3 start, Vector3 end, Vector3 incoming, Vector3 outgoing)
    {
        var gap = Vector3.Distance(start, end);
        var handle = Math.Clamp(gap * .65f + 1.5f, 1.5f, 5f);
        var controlA = start + incoming * handle;
        var controlB = end - outgoing * handle;
        var points = new Vector3[6];
        for (var i = 0; i < points.Length; i++)
        {
            var t = (i + 1f) / points.Length;
            var u = 1 - t;
            points[i] = start * (u * u * u) + controlA * (3 * u * u * t)
                + controlB * (3 * u * t * t) + end * (t * t * t);
        }
        points[^1] = end;
        return points;
    }

    private static double PolylineLength(IReadOnlyList<Vector3> points)
    {
        double length = 0;
        for (var i = 1; i < points.Count; i++) length += Vector3.Distance(points[i - 1], points[i]);
        return length;
    }

    private static Vector3 LanePosition(Vector3 position, Vector3 forward, float offset) =>
        position + LocalLeft(forward) * offset;

    public static IReadOnlyList<AmbientActorPath> BuildFootpathPaths(IReadOnlyList<AmbientRouteSample> samples,
        double routeLengthMeters, double lateralOffsetMeters = PedestrianFootpathOffsetMeters,
        double heightMeters = PedestrianFootpathHeightMeters, double exclusionMeters = DefaultSeamExclusionMeters)
    {
        if (samples.Count < 3 || !double.IsFinite(routeLengthMeters) || routeLengthMeters <= 0
            || !double.IsFinite(lateralOffsetMeters) || lateralOffsetMeters <= 0
            || !double.IsFinite(heightMeters) || !double.IsFinite(exclusionMeters) || exclusionMeters < 0)
            return [];
        var exclusions = FindDiscontinuities(samples, routeLengthMeters)
            .SelectMany(d => d <= 0.001 || routeLengthMeters - d <= 0.001
                ? new[] { (0d, Math.Min(routeLengthMeters, exclusionMeters + .001)), (Math.Max(0, routeLengthMeters - exclusionMeters - .001), routeLengthMeters) }
                : new[] { (Math.Max(0, d - exclusionMeters - .001), Math.Min(routeLengthMeters, d + exclusionMeters + .001)) })
            .ToArray();
        var intervals = SubtractIntervals((0, routeLengthMeters), exclusions);
        var result = new List<AmbientActorPath>();
        var pathId = 0L;
        foreach (var interval in intervals.Where(i => i.End - i.Start >= 12))
        foreach (var side in new[] { -1f, 1f })
        {
            var distances = new List<double> { interval.Start };
            distances.AddRange(samples.Where(s => s.DistanceMeters > interval.Start && s.DistanceMeters < interval.End)
                .Select(s => s.DistanceMeters));
            distances.Add(interval.End);
            var points = distances.Select(distance => SampleRoute(samples, distance, routeLengthMeters))
                .Select(sample => sample.Position + LocalLeft(sample.Forward) * (side * (float)lateralOffsetMeters)
                    + Vector3.UnitY * (float)heightMeters).ToArray();
            var id = ++pathId;
            Add(points, 1); Add(points.Reverse().ToArray(), -1);

            void Add(IEnumerable<Vector3> pathPoints, int direction)
            {
                var path = new AmbientActorPath(AmbientActorKind.Pedestrian, id, direction, pathPoints,
                    new AmbientSpeedRange(PedestrianMinimumSpeedMetersPerSecond, PedestrianMaximumSpeedMetersPerSecond));
                if (path.IsUsable) result.Add(path);
            }
        }
        return result;
    }

    public static int CountFootpathSamplesExcludedNearSeams(IReadOnlyList<AmbientRouteSample> samples,
        double routeLengthMeters, double exclusionMeters = DefaultSeamExclusionMeters)
    {
        if (samples.Count < 3 || !double.IsFinite(routeLengthMeters) || routeLengthMeters <= 0
            || !double.IsFinite(exclusionMeters) || exclusionMeters < 0) return 0;
        var discontinuities = FindDiscontinuities(samples, routeLengthMeters);
        return samples.Count(sample => discontinuities.Any(distance =>
        {
            var delta = Math.Abs(sample.DistanceMeters - distance);
            delta = Math.Min(delta, routeLengthMeters - delta);
            return delta <= exclusionMeters + .001;
        }));
    }

    public static bool TrySelectSpawn(IReadOnlyList<AmbientActorPath> paths, Vector3 riderPosition,
        float minimumDistanceMeters, float maximumDistanceMeters, Random random, int attempts,
        out AmbientSpawnSelection selection) => TrySelectSpawn(paths, riderPosition, minimumDistanceMeters,
        maximumDistanceMeters, random, attempts, out selection, out _, out _, out _);

    public static bool TrySelectSpawn(IReadOnlyList<AmbientActorPath> paths, Vector3 riderPosition,
        float minimumDistanceMeters, float maximumDistanceMeters, Random random, int attempts,
        out AmbientSpawnSelection selection, out int rejectedTooClose, out int rejectedOutsideRadius,
        out int rejectedInvalidPath)
    {
        selection = default;
        rejectedTooClose = rejectedOutsideRadius = rejectedInvalidPath = 0;
        if (paths.Count == 0 || !Finite(riderPosition) || minimumDistanceMeters < 0
            || maximumDistanceMeters < minimumDistanceMeters || attempts <= 0) return false;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var index = random.Next(paths.Count);
            var path = paths[index];
            if (!path.IsUsable) { rejectedInvalidPath++; continue; }
            var distance = random.NextDouble() * path.LengthMeters;
            var position = path.Sample(distance).Position;
            var riderDistance = Vector3.Distance(position, riderPosition);
            if (riderDistance < minimumDistanceMeters) { rejectedTooClose++; continue; }
            if (riderDistance > maximumDistanceMeters) { rejectedOutsideRadius++; continue; }
            selection = new AmbientSpawnSelection(index, distance, position);
            return true;
        }
        return false;
    }

    public static AmbientActorState Advance(AmbientActorState state, AmbientActorPath path,
        double distanceAlongPathMeters, double deltaSeconds, bool paused)
    {
        if (!state.Active || !path.IsUsable) return state;
        var nextDistance = AdvancePathDistance(path, distanceAlongPathMeters, state.SpeedMetersPerSecond, deltaSeconds, paused);
        if (nextDistance == distanceAlongPathMeters) return state;
        if (!double.IsFinite(nextDistance))
            return state with { Active = false };
        if (path.LoopStartDistanceMeters is null && nextDistance >= path.LengthMeters) return state with { Active = false };
        var (position, heading) = path.Sample(nextDistance);
        return state with { WorldPosition = position, Heading = heading };
    }

    public static double AdvancePathDistance(AmbientActorPath path, double distanceAlongPathMeters,
        float speedMetersPerSecond, double deltaSeconds, bool paused)
    {
        if (!path.IsUsable || paused || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0
            || !float.IsFinite(speedMetersPerSecond) || speedMetersPerSecond <= 0) return distanceAlongPathMeters;
        var nextDistance = distanceAlongPathMeters + speedMetersPerSecond * deltaSeconds;
        if (!double.IsFinite(nextDistance)) return double.NaN;
        if (path.LoopStartDistanceMeters is { } loopStart && nextDistance >= path.LengthMeters)
        {
            var loopLength = path.LengthMeters - loopStart;
            return loopStart + ((nextDistance - path.LengthMeters) % loopLength);
        }
        return nextDistance;
    }

    public static bool IsVisible(Vector3 actorPosition, Vector3 riderPosition, float radiusMeters) =>
        Finite(actorPosition) && Finite(riderPosition) && float.IsFinite(radiusMeters) && radiusMeters >= 0
        && Vector3.DistanceSquared(actorPosition, riderPosition) <= radiusMeters * radiusMeters;

    public static bool IsValidTransform(Vector3 position, Vector3 heading) => Finite(position) && Finite(heading)
        && heading.LengthSquared() > 1e-10f;

    private static IReadOnlyList<Vector3> OffsetTravelPath(IReadOnlyList<Vector3> source, float offset)
    {
        var points = source.Where(Finite).ToArray();
        var result = new Vector3[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var tangent = i == 0 ? points[Math.Min(1, points.Length - 1)] - points[0]
                : i == points.Length - 1 ? points[i] - points[i - 1] : points[i + 1] - points[i - 1];
            var left = LocalLeft(tangent);
            result[i] = points[i] + left * offset;
        }
        return result;
    }

    private static IReadOnlyList<double> FindDiscontinuities(IReadOnlyList<AmbientRouteSample> samples, double length)
    {
        var found = new List<double>();
        var last = samples.Count - 1;
        for (var i = 0; i < last; i++)
        {
            var previous = samples[i == 0 ? last - 1 : i - 1].Position;
            var current = samples[i].Position;
            var next = samples[i + 1].Position;
            if (JoinAngleDegrees(previous, current, next) >= 60) found.Add(samples[i].DistanceMeters);
        }
        if (samples[^1].DistanceMeters < length - .001 && Vector3.DistanceSquared(samples[0].Position, samples[^1].Position) < 1e-4f)
            found.Add(0);
        return found;
    }

    private static float JoinAngleDegrees(Vector3 previous, Vector3 current, Vector3 next)
    {
        var incoming = Horizontal(current - previous); var outgoing = Horizontal(next - current);
        if (incoming.LengthSquared() < 1e-8f || outgoing.LengthSquared() < 1e-8f) return 0;
        var cosine = Math.Clamp(Vector3.Dot(Vector3.Normalize(incoming), Vector3.Normalize(outgoing)), -1, 1);
        return MathF.Acos(cosine) * (180 / MathF.PI);
    }

    private static IEnumerable<(double Start, double End)> SubtractIntervals((double Start, double End) source,
        IEnumerable<(double Start, double End)> exclusions)
    {
        var allowed = new List<(double Start, double End)> { source };
        foreach (var exclusion in exclusions)
        {
            var next = new List<(double Start, double End)>();
            foreach (var interval in allowed)
            {
                if (exclusion.End <= interval.Start || exclusion.Start >= interval.End) next.Add(interval);
                else
                {
                    if (exclusion.Start > interval.Start) next.Add((interval.Start, exclusion.Start));
                    if (exclusion.End < interval.End) next.Add((exclusion.End, interval.End));
                }
            }
            allowed = next;
        }
        return allowed;
    }

    private static AmbientRouteSample SampleRoute(IReadOnlyList<AmbientRouteSample> samples, double distance, double length)
    {
        var d = Math.Clamp(distance, 0, length);
        var low = 0; var high = samples.Count - 1;
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (samples[middle].DistanceMeters <= d) low = middle; else high = middle;
        }
        var a = samples[low]; var b = samples[high];
        var span = b.DistanceMeters - a.DistanceMeters;
        var t = span > 0 ? (float)((d - a.DistanceMeters) / span) : 0;
        var tangent = b.Position - a.Position;
        if (tangent.LengthSquared() < 1e-10f) tangent = Vector3.UnitX;
        return new AmbientRouteSample(d, Vector3.Lerp(a.Position, b.Position, t), Vector3.Normalize(tangent));
    }

    private static Vector3 LocalLeft(Vector3 forward)
    {
        var horizontal = Horizontal(forward);
        if (horizontal.LengthSquared() < 1e-10f) return Vector3.Zero;
        return Vector3.Normalize(Vector3.Cross(Vector3.UnitY, horizontal));
    }
    private static Vector3 Horizontal(Vector3 value) => new(value.X, 0, value.Z);
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static AmbientSpeedRange Kph(float min, float max) => new(min / 3.6f, max / 3.6f);
}

public static class AmbientActorPathDirection
{
    public const int Forward = 1;
    public const int Reverse = -1;
}
