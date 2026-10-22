using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NVector3 = System.Numerics.Vector3;

/// <summary>Local, non-collidable ambient scenery. It consumes rider position only and never feeds ride state.</summary>
public partial class AmbientActorManager : Node3D
{
    [Export] public int RandomSeed { get; set; } = AmbientActorTuning.DefaultSeed;

    private sealed class ActiveActor
    {
        public required AmbientActorState State;
        public required AmbientActorPath Path;
        public required double DistanceAlongPath;
        public required float CruiseSpeedMetersPerSecond;
        public required Node3D Visual;
        public float LaneOffsetMeters;
        public string? OvertakingRiderId;
        public string? YieldingToPlayerId;
    }

    private readonly List<ActiveActor> _active = [];
    private readonly List<AmbientActorPath> _carPaths = [];
    private readonly List<AmbientActorPath> _pedestrianPaths = [];
    private Random _random = new(AmbientActorTuning.DefaultSeed);
    private int _nextActorId = 1;
    private bool _configured;

    public int ActiveCarCount => _active.Count(actor => actor.State.Active && actor.State.Kind == AmbientActorKind.Car);
    public int ActivePedestrianCount => _active.Count(actor => actor.State.Active && actor.State.Kind == AmbientActorKind.Pedestrian);
    public int ActiveActorCount => _active.Count;
    public int CarPathCount => _carPaths.Count;
    public int PedestrianPathCount => _pedestrianPaths.Count;
    public long CarSpawnAttempts { get; private set; }
    public long PedestrianSpawnAttempts { get; private set; }
    public long CarOutsideSpawnRadius { get; private set; }
    public long PedestrianOutsideSpawnRadius { get; private set; }
    public long CarTooCloseToRider { get; private set; }
    public long PedestrianTooCloseToRider { get; private set; }
    public long CarInvalidTransform { get; private set; }
    public long PedestrianInvalidTransform { get; private set; }
    public long CarNoEligiblePath { get; private set; }
    public long PedestrianNoValidPath { get; private set; }
    public long CarIllegalOnewayDirections { get; private set; }
    public int PedestrianSeamOrEndSamplesExcluded { get; private set; }
    public int EligibleStreetPolylines { get; private set; }
    public int TotalStreetPolylines { get; private set; }
    public int EligiblePedestrianSections => PedestrianPathCount / 4;
    public int InvalidOrShortPedestrianPaths { get; private set; }
    public int InvalidOrShortCarPaths { get; private set; }
    public int CarPathsContinuingOntoRoute { get; private set; }
    public AmbientActorState? FirstCarState => _active.FirstOrDefault(actor => actor.State.Kind == AmbientActorKind.Car)?.State;
    public float? FirstCarLaneOffset => _active.FirstOrDefault(actor => actor.State.Kind == AmbientActorKind.Car)?.LaneOffsetMeters;
    internal IReadOnlyList<AmbientActorState> TrafficTestStates => _active.Select(actor => actor.State).ToArray();
    public AmbientActorState? FirstPedestrianState => _active.FirstOrDefault(actor => actor.State.Kind == AmbientActorKind.Pedestrian)?.State;
    public string? FirstCarRoadClass => _active.FirstOrDefault(actor => actor.State.Kind == AmbientActorKind.Car)?.Path.SourceClass;
    public string? FirstCarRoadName => _active.FirstOrDefault(actor => actor.State.Kind == AmbientActorKind.Car)?.Path.SourceName;
    public bool FirstCarVisible => FirstVisualVisible(AmbientActorKind.Car);
    public bool FirstPedestrianVisible => FirstVisualVisible(AmbientActorKind.Pedestrian);
    public int ApproximateTriangleCount => ActiveCarCount * AmbientCarVisual.ApproximateTriangleCount
        + ActivePedestrianCount * AmbientPedestrianVisual.ApproximateTriangleCount;
    internal IReadOnlyList<AmbientTrafficRider> GetTrafficReviewSamples() =>
        _active.Where(actor => actor.State.Active).Select(ToTrafficRider).ToArray();

    public void Configure(VisualRoute route, RouteBackgroundStreetData streetData)
    {
        if (_configured) throw new InvalidOperationException("Ambient actor manager is already configured.");
        _configured = true;
        _random = new Random(RandomSeed);
        TotalStreetPolylines = streetData.Streets.Count;
        var routeSamples = route.Samples.Select(sample => new AmbientRouteSample(sample.DistanceMeters,
            ToNumerics(ToGlobal(ProceduralRoad.ToGodot(sample.Position))), sample.Forward)).ToArray();
        var routeIndex = new BackgroundStreetGeometry.RouteSegmentSpatialIndex(route.Samples);
        var roadInputs = streetData.Streets.Select(street =>
        {
            var connected = BackgroundStreetGeometry.ConnectRouteEnds(
                BackgroundStreetGeometry.ConvertPoints(route, street.Points), route.Samples, spatialIndex: routeIndex);
            var routeConnections = connected.Connections.Select(connection => new AmbientRoadRouteConnection(
                connection.EndIndex, connection.RouteDistanceMeters,
                ToNumerics(ToGlobal(ProceduralRoad.ToGodot(connection.Position))))).ToArray();
            return new AmbientRoadInput(street.OsmId, street.Highway, street.Oneway, street.WidthMeters,
                connected.Points.Select(point => ToNumerics(ToGlobal(ProceduralRoad.ToGodot(point)))).ToArray(),
                street.Name, routeConnections, street.OnewayDirection);
        }).ToArray();
        EligibleStreetPolylines = roadInputs.Count(input => AmbientActorMath.IsEligibleRoad(input.Highway));
        CarIllegalOnewayDirections = streetData.Streets.Count(street => street.Oneway
            && AmbientActorMath.IsEligibleRoad(street.Highway));
        _carPaths.AddRange(AmbientActorMath.BuildCarPaths(roadInputs, routeSamples, route.LengthMeters));
        CarPathsContinuingOntoRoute = _carPaths.Count(path => path.ContinuesAroundRoute);
        GameLogger.Log("ambient_car_paths_configured", new
        {
            routeId = streetData.Id,
            sourcePolylines = streetData.Streets.Count,
            carPaths = _carPaths.Count,
            pathsContinuingOntoRoute = CarPathsContinuingOntoRoute,
            routeLengthMeters = route.LengthMeters,
            oneWayPolylines = CarIllegalOnewayDirections
        });
        PedestrianSeamOrEndSamplesExcluded = AmbientActorMath.CountFootpathSamplesExcludedNearSeams(
            routeSamples, route.LengthMeters, ProceduralRoad.NonAsphaltJoinExclusionMeters);
        _pedestrianPaths.AddRange(AmbientActorMath.BuildFootpathPaths(routeSamples, route.LengthMeters,
            exclusionMeters: ProceduralRoad.NonAsphaltJoinExclusionMeters));
    }

    public void UpdateForCyclists(IReadOnlyList<AmbientTrafficRider> cyclists, double deltaSeconds, bool paused)
    {
        if (!_configured || paused || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0) return;
        var player = cyclists.FirstOrDefault(rider => rider.RiderId == "player");
        if (!Finite(player.Position)) return;
        UpdateAndCull(_active, cyclists, deltaSeconds);
        EnsurePopulation(AmbientActorKind.Car, _carPaths, player.Position, cyclists,
            AmbientActorTuning.TargetCars, AmbientActorTuning.MaximumCars,
            AmbientActorTuning.CarMinimumSpawnDistanceMeters, AmbientActorTuning.CarSpawnRadiusMeters,
            AmbientActorTuning.CarDespawnRadiusMeters);
        EnsurePopulation(AmbientActorKind.Pedestrian, _pedestrianPaths, player.Position, cyclists,
            AmbientActorTuning.TargetPedestrians, AmbientActorTuning.MaximumPedestrians,
            AmbientActorTuning.PedestrianMinimumSpawnDistanceMeters, AmbientActorTuning.PedestrianSpawnRadiusMeters,
            AmbientActorTuning.PedestrianDespawnRadiusMeters);
    }

    private void UpdateAndCull(List<ActiveActor> actors, IReadOnlyList<AmbientTrafficRider> cyclists, double deltaSeconds)
    {
        var playerPosition = cyclists.FirstOrDefault(rider => rider.RiderId == "player").Position;
        var activeTraffic = actors.Where(actor => actor.State.Active).Select(ToTrafficRider).ToArray();
        var pedestrians = activeTraffic.Where(actor => actor.RiderId.StartsWith("pedestrian-", StringComparison.Ordinal)).ToArray();
        var cars = activeTraffic.Where(actor => actor.RiderId.StartsWith("car-", StringComparison.Ordinal)).ToArray();
        for (var i = actors.Count - 1; i >= 0; i--)
        {
            var actor = actors[i];
            var speed = actor.State.SpeedMetersPerSecond;
            if (actor.State.Kind == AmbientActorKind.Car)
            {
                var otherCars = cars.Where(car => car.RiderId != $"car-{actor.State.ActorId}").ToArray();
                var obstacles = cyclists.Concat(pedestrians).Concat(otherCars).ToArray();
                // Give crossing cars a stable, deterministic order. The higher-ID car
                // yields to the lower-ID car; without this, reciprocal look-ahead can
                // make both vehicles brake to a stop at the edge of the same junction.
                // Keep full geometry checks below so the priority vehicle still cannot
                // enter an occupied envelope during an update.
                var rightOfWayObstacles = cyclists.Concat(pedestrians)
                    .Concat(otherCars.Where(car => !TryGetCarActorId(car.RiderId, out var otherId)
                        || otherId < actor.State.ActorId
                        || NVector3.Dot(NormalizeHorizontal(actor.State.Heading), NormalizeHorizontal(car.Forward)) > .55f))
                    .ToArray();
                if (actor.OvertakingRiderId is { } overtakingId)
                {
                    var targetRider = cyclists.FirstOrDefault(rider => rider.RiderId == overtakingId);
                    var passed = targetRider.RiderId is null || IsPastOvertakenActor(actor, targetRider);
                    var laneTarget = passed ? actor.Path.BaseLaneOffsetMeters : OvertakeLaneOffset(actor.Path);
                    actor.LaneOffsetMeters = ApproachLane(actor.LaneOffsetMeters, laneTarget, deltaSeconds);
                    // Keep the manoeuvre active through the entire return transition. If the
                    // target is cleared after the first 60 mm lane step, later frames have no
                    // state that continues moving the car back to its normal travel lane.
                    if (passed && Math.Abs(actor.LaneOffsetMeters - actor.Path.BaseLaneOffsetMeters) < .05f)
                        actor.OvertakingRiderId = null;
                }
                else if (FindSafeOvertakeTarget(actor, cyclists, pedestrians, otherCars) is { } overtakeTarget)
                {
                    actor.OvertakingRiderId = overtakeTarget;
                }
                if (actor.YieldingToPlayerId is { } yieldingPlayerId)
                {
                    var player = cyclists.FirstOrDefault(rider => rider.RiderId == yieldingPlayerId);
                    var passed = player.RiderId is null || IsCyclistPastYieldingCar(actor, player);
                    var laneTarget = passed ? actor.Path.BaseLaneOffsetMeters : SafeYieldLaneOffset(actor.Path);
                    actor.LaneOffsetMeters = ApproachLane(actor.LaneOffsetMeters, laneTarget, deltaSeconds);
                    if (passed && Math.Abs(actor.LaneOffsetMeters - actor.Path.BaseLaneOffsetMeters) < .05f)
                        actor.YieldingToPlayerId = null;
                }
                else if (FindSafeYieldingPlayer(actor, cyclists, pedestrians, otherCars) is { } yieldingPlayer)
                {
                    actor.YieldingToPlayerId = yieldingPlayer;
                    actor.LaneOffsetMeters = ApproachLane(actor.LaneOffsetMeters,
                        SafeYieldLaneOffset(actor.Path), deltaSeconds);
                }
                var targetSpeed = AmbientTrafficFollowingMath.TargetSpeed(actor.Path, actor.DistanceAlongPath,
                    actor.LaneOffsetMeters, actor.CruiseSpeedMetersPerSecond, rightOfWayObstacles);
                if (actor.YieldingToPlayerId is not null)
                    targetSpeed = Math.Min(targetSpeed, actor.State.SpeedMetersPerSecond);
                speed = AmbientTrafficFollowingMath.ApproachSpeed(speed, targetSpeed, deltaSeconds);
                speed = AmbientTrafficFollowingMath.ClampSpeedForClearance(actor.Path, actor.DistanceAlongPath,
                    speed, deltaSeconds, cyclists, pedestrians, otherCars, actor.LaneOffsetMeters);
            }
            var movingState = actor.State with { SpeedMetersPerSecond = speed };
            var nextDistance = AmbientActorMath.AdvancePathDistance(actor.Path, actor.DistanceAlongPath,
                speed, deltaSeconds, paused: false);
            var nextState = AmbientActorMath.Advance(movingState, actor.Path, actor.DistanceAlongPath, deltaSeconds, paused: false);
            var lanePose = actor.Path.SampleLane(nextDistance, actor.LaneOffsetMeters);
            nextState = nextState with { WorldPosition = lanePose.Position, Heading = lanePose.Heading };
            var visible = AmbientActorMath.IsVisible(ToNumerics(nextState.WorldPosition), playerPosition,
                actor.State.Kind == AmbientActorKind.Car ? AmbientActorTuning.CarDespawnRadiusMeters : AmbientActorTuning.PedestrianDespawnRadiusMeters);
            if (actor.Path.LoopStartDistanceMeters is { } loopStart && actor.DistanceAlongPath < loopStart && nextDistance >= loopStart)
                GameLogger.Log("ambient_car_junction_transition", new
                {
                    actorId = actor.State.ActorId, sourceOsmId = actor.Path.SourceId, sourceName = actor.Path.SourceName,
                    roadClass = actor.Path.SourceClass, direction = actor.Path.Direction,
                    routeDistanceMeters = actor.Path.RouteConnectionDistanceMeters,
                    transition = "side-street-to-route", pathDistanceMeters = nextDistance
                });
            if (!nextState.Active || !visible)
            {
                var distanceToRider = NVector3.Distance(ToNumerics(nextState.WorldPosition), playerPosition);
                GameLogger.Log("ambient_actor_despawned", new
                {
                    actorId = actor.State.ActorId, kind = actor.State.Kind.ToString(), sourceOsmId = actor.Path.SourceId,
                    sourceName = actor.Path.SourceName, roadClass = actor.Path.SourceClass, direction = actor.Path.Direction,
                    pathDistanceMeters = nextDistance, pathLengthMeters = actor.Path.LengthMeters,
                    routeLoopContinuation = actor.Path.ContinuesAroundRoute, routeDistanceMeters = actor.Path.RouteConnectionDistanceMeters,
                    riderDistanceMeters = distanceToRider,
                    reason = !nextState.Active ? "path-endpoint" : "outside-rider-visibility-radius"
                });
                actor.Visual.QueueFree(); actors.RemoveAt(i); continue;
            }
            actor.DistanceAlongPath = nextDistance;
            actor.State = nextState;
            ApplyPose(actor);
        }
    }

    private static AmbientTrafficRider ToTrafficRider(ActiveActor actor)
    {
        var isCar = actor.State.Kind == AmbientActorKind.Car;
        var length = isCar ? AmbientActorEnvelopeMath.CarLengthMeters : AmbientActorEnvelopeMath.PedestrianLengthMeters;
        var width = isCar ? AmbientActorEnvelopeMath.CarWidthMeters : AmbientActorEnvelopeMath.PedestrianWidthMeters;
        var height = isCar ? AmbientActorEnvelopeMath.CarHeightMeters : AmbientActorEnvelopeMath.PedestrianHeightMeters;
        var predictionSamples = (int)Math.Ceiling(AmbientTrafficFollowingMath.PredictionHorizonSeconds
            / AmbientTrafficFollowingMath.PredictionSampleSeconds);
        var prediction = new List<AmbientTrafficPose>(predictionSamples + 1);
        for (var sample = 0; sample <= predictionSamples; sample++)
        {
            var seconds = sample * AmbientTrafficFollowingMath.PredictionSampleSeconds;
            var distance = AmbientActorMath.AdvancePathDistance(actor.Path, actor.DistanceAlongPath,
                actor.State.SpeedMetersPerSecond, seconds, paused: false);
            var (position, heading) = actor.Path.SampleLane(distance, actor.LaneOffsetMeters);
            prediction.Add(new AmbientTrafficPose(seconds, position, heading));
        }
        var kind = isCar ? "car" : "pedestrian";
        return new AmbientTrafficRider($"{kind}-{actor.State.ActorId}",
            ToNumerics(actor.State.WorldPosition), ToNumerics(actor.State.Heading), actor.State.SpeedMetersPerSecond,
            length, width, height, prediction);
    }

    private static bool TryGetCarActorId(string riderId, out long actorId)
    {
        actorId = 0;
        return riderId.StartsWith("car-", StringComparison.Ordinal)
            && long.TryParse(riderId.AsSpan(4), out actorId);
    }

    private static string? FindSafeOvertakeTarget(ActiveActor car,
        IReadOnlyList<AmbientTrafficRider> cyclists, IReadOnlyList<AmbientTrafficRider> pedestrians,
        IReadOnlyList<AmbientTrafficRider> otherCars)
    {
        if (car.Path.RoadWidthMeters < 6.5 || !car.Path.IsUsable) return null;
        var passLane = OvertakeLaneOffset(car.Path);
        if (Math.Abs(passLane) + AmbientActorEnvelopeMath.CarWidthMeters * .5
            + AmbientActorMath.RoadEdgeClearanceMeters > car.Path.RoadWidthMeters * .5 + .001) return null;
        var (_, currentHeading) = car.Path.SampleLane(car.DistanceAlongPath, car.LaneOffsetMeters);
        var currentPosition = ToNumerics(car.State.WorldPosition);
        var candidate = cyclists.Select(rider =>
            {
                var relative = rider.Position - currentPosition;
                var forwardGap = NVector3.Dot(relative, currentHeading);
                var lateralGap = Math.Abs(NVector3.Dot(relative, LocalLeft(currentHeading)));
                var passPosition = car.Path.SampleLane(car.DistanceAlongPath, passLane).Position;
                var passLateralGap = Math.Abs(NVector3.Dot(rider.Position - passPosition,
                    LocalLeft(currentHeading)));
                return (Rider: rider, ForwardGap: forwardGap, LateralGap: lateralGap,
                    PassLateralGap: passLateralGap,
                    Direction: NVector3.Dot(NormalizeHorizontal(currentHeading), NormalizeHorizontal(rider.Forward)));
            })
            .Where(item => item.Direction > .65f && item.ForwardGap is >= 7 and <= 35
                && item.LateralGap < 1.6f
                && item.PassLateralGap >= AmbientActorEnvelopeMath.CarWidthMeters * .5f
                    + item.Rider.WidthMeters * .5f + AmbientActorEnvelopeMath.SafetyMarginMeters
                && car.CruiseSpeedMetersPerSecond - item.Rider.SpeedMetersPerSecond > 2.0f)
            .OrderBy(item => item.ForwardGap).FirstOrDefault();
        if (candidate.Rider.RiderId is null) return null;

        // Refuse the pass if any other actor occupies the center-side swept corridor until
        // the car is safely ahead. That includes oncoming traffic on the opposite lane.
        var relativeSpeed = Math.Max(2f, car.CruiseSpeedMetersPerSecond - candidate.Rider.SpeedMetersPerSecond);
        var duration = (candidate.ForwardGap + AmbientActorEnvelopeMath.CarLengthMeters + candidate.Rider.LengthMeters
            + AmbientTrafficFollowingMath.MinimumFollowingGapMeters * 2 + 3f) / relativeSpeed;
        var horizon = Math.Clamp(duration + 1.0, 1.0, 12.0);
        var steps = Math.Max(1, (int)Math.Ceiling(horizon / .15));
        var conflicts = pedestrians.Concat(otherCars)
            .Concat(cyclists.Where(rider => rider.RiderId != candidate.Rider.RiderId)).ToArray();
        for (var step = 0; step <= steps; step++)
        {
            var seconds = horizon * step / steps;
            var distance = AmbientActorMath.AdvancePathDistance(car.Path, car.DistanceAlongPath,
                Math.Max(car.CruiseSpeedMetersPerSecond, car.State.SpeedMetersPerSecond), seconds, paused: false);
            var pose = car.Path.SampleLane(distance, passLane);
            if (!AmbientActorMath.IsValidTransform(pose.Position, pose.Heading)) return null;
            if (conflicts.Any(obstacle => AmbientActorEnvelopeMath.Overlaps(pose.Position, pose.Heading,
                    AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
                    AmbientActorEnvelopeMath.CarHeightMeters, obstacle.PoseAt(seconds),
                    obstacle.LengthMeters, obstacle.WidthMeters, obstacle.HeightMeters))) return null;
        }
        return candidate.Rider.RiderId;
    }

    private static bool IsPastOvertakenActor(ActiveActor car, AmbientTrafficRider rider)
    {
        var relative = ToNumerics(car.State.WorldPosition) - rider.Position;
        var longitudinal = NVector3.Dot(relative, NormalizeHorizontal(rider.Forward));
        return longitudinal > AmbientActorEnvelopeMath.CarLengthMeters * .5f
            + rider.LengthMeters * .5f + AmbientTrafficFollowingMath.MinimumFollowingGapMeters;
    }

    private static string? FindSafeYieldingPlayer(ActiveActor car,
        IReadOnlyList<AmbientTrafficRider> cyclists, IReadOnlyList<AmbientTrafficRider> pedestrians,
        IReadOnlyList<AmbientTrafficRider> otherCars)
    {
        if (!car.Path.IsUsable || car.Path.RoadWidthMeters < 5.5) return null;
        var laneTarget = SafeYieldLaneOffset(car.Path);
        if (Math.Abs(laneTarget - car.LaneOffsetMeters) < .45f) return null;
        var (_, heading) = car.Path.SampleLane(car.DistanceAlongPath, car.LaneOffsetMeters);
        var candidate = cyclists.Where(rider => rider.RiderId == "player")
            .Select(rider =>
            {
                var relative = rider.Position - ToNumerics(car.State.WorldPosition);
                var behindGap = -NVector3.Dot(relative, heading);
                var sameDirection = NVector3.Dot(NormalizeHorizontal(heading), NormalizeHorizontal(rider.Forward));
                var closingSpeed = rider.SpeedMetersPerSecond - car.State.SpeedMetersPerSecond;
                var timeToCatch = closingSpeed > .1f ? behindGap / closingSpeed : float.PositiveInfinity;
                return (Rider: rider, BehindGap: behindGap, SameDirection: sameDirection,
                    ClosingSpeed: closingSpeed, TimeToCatch: timeToCatch);
            })
            .Where(item => item.SameDirection > .7f && item.BehindGap is >= 8 and <= 55
                && item.ClosingSpeed >= 2 && item.TimeToCatch <= 8)
            .OrderBy(item => item.TimeToCatch).FirstOrDefault();
        if (candidate.Rider.RiderId is null) return null;

        // A yielding car moves only toward the road centre. Refuse the move if the
        // entire short swept corridor is occupied by oncoming traffic or another actor.
        var obstacles = cyclists.Where(rider => rider.RiderId != candidate.Rider.RiderId)
            .Concat(pedestrians).Concat(otherCars).ToArray();
        const double horizon = 2.5;
        var steps = 10;
        for (var step = 0; step <= steps; step++)
        {
            var seconds = horizon * step / steps;
            var distance = AmbientActorMath.AdvancePathDistance(car.Path, car.DistanceAlongPath,
                car.State.SpeedMetersPerSecond, seconds, paused: false);
            var laneShift = Math.Clamp(laneTarget - car.LaneOffsetMeters,
                (float)(-1.2 * seconds), (float)(1.2 * seconds));
            var pose = car.Path.SampleLane(distance, car.LaneOffsetMeters + laneShift);
            if (!AmbientActorMath.IsValidTransform(pose.Position, pose.Heading)
                || obstacles.Any(obstacle => AmbientActorEnvelopeMath.Overlaps(pose.Position, pose.Heading,
                    AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
                    AmbientActorEnvelopeMath.CarHeightMeters, obstacle.PoseAt(seconds),
                    obstacle.LengthMeters, obstacle.WidthMeters, obstacle.HeightMeters)))
                return null;
        }
        return candidate.Rider.RiderId;
    }

    internal static bool WouldSpawnIntoImmediatePlayerCatch(NVector3 carPosition, NVector3 carForward,
        float slowestCarSpeed, IReadOnlyList<AmbientTrafficRider> cyclists)
    {
        var forward = NormalizeHorizontal(carForward);
        foreach (var player in cyclists.Where(rider => rider.RiderId == "player"))
        {
            var playerForward = NormalizeHorizontal(player.Forward);
            if (NVector3.Dot(forward, playerForward) < .7f) continue;
            var relative = carPosition - player.Position;
            var ahead = NVector3.Dot(relative, playerForward);
            var lateral = Math.Abs(NVector3.Dot(relative, LocalLeft(playerForward)));
            var closingSpeed = player.SpeedMetersPerSecond - slowestCarSpeed;
            if (ahead is > 8 and <= 80 && lateral < 3.2f && closingSpeed >= 2
                && ahead / closingSpeed < 8)
                return true;
        }
        return false;
    }

    private static bool IsCyclistPastYieldingCar(ActiveActor car, AmbientTrafficRider rider)
    {
        var longitudinal = NVector3.Dot(ToNumerics(car.State.WorldPosition) - rider.Position,
            NormalizeHorizontal(rider.Forward));
        return longitudinal < -(AmbientActorEnvelopeMath.CarLengthMeters * .5f
            + rider.LengthMeters * .5f + AmbientTrafficFollowingMath.MinimumFollowingGapMeters);
    }

    private static float SafeYieldLaneOffset(AmbientActorPath path)
    {
        // Centreward motion gives a faster cyclist room on the road-side of the car.
        // The swept-envelope check above rejects the move when that space is occupied.
        var halfTravelableWidth = Math.Max(0, (float)(path.RoadWidthMeters * .5
            - AmbientActorEnvelopeMath.CarWidthMeters * .5 - AmbientActorMath.RoadEdgeClearanceMeters));
        return Math.Clamp(0, -halfTravelableWidth, halfTravelableWidth);
    }

    private static float OvertakeLaneOffset(AmbientActorPath path) =>
        Math.Clamp(Math.Sign(path.BaseLaneOffsetMeters) * .15f,
            (float)(-path.RoadWidthMeters * .5 + AmbientActorEnvelopeMath.CarWidthMeters * .5 + AmbientActorMath.RoadEdgeClearanceMeters),
            (float)(path.RoadWidthMeters * .5 - AmbientActorEnvelopeMath.CarWidthMeters * .5 - AmbientActorMath.RoadEdgeClearanceMeters));

    private static float ApproachLane(float current, float target, double deltaSeconds)
    {
        var maxStep = (float)(1.2 * Math.Clamp(deltaSeconds, 0, .25));
        return current + Math.Clamp(target - current, -maxStep, maxStep);
    }

    private static NVector3 LocalLeft(NVector3 forward)
    {
        var left = NVector3.Cross(NVector3.UnitY, forward);
        return left.LengthSquared() > 1e-8f ? NVector3.Normalize(left) : NVector3.Zero;
    }

    private static NVector3 NormalizeHorizontal(NVector3 value)
    {
        value.Y = 0;
        return value.LengthSquared() > 1e-8f ? NVector3.Normalize(value) : NVector3.Zero;
    }

    private void EnsurePopulation(AmbientActorKind kind, IReadOnlyList<AmbientActorPath> paths,
        NVector3 riderPosition, IReadOnlyList<AmbientTrafficRider> cyclists, int target, int maximum, float minimumSpawnDistance,
        float spawnRadius, float despawnRadius)
    {
        var activeCount = _active.Count(actor => actor.State.Kind == kind && actor.State.Active);
        if (activeCount >= target || activeCount >= maximum) return;
        if (paths.Count == 0)
        {
            if (kind == AmbientActorKind.Car) CarNoEligiblePath++; else PedestrianNoValidPath++;
            return;
        }
        var attempts = Math.Min(100, paths.Count * 3);
        while (activeCount < target && activeCount < maximum)
        {
            var tooClose = 0;
            var outsideRadius = 0;
            var invalidPath = 0;
            var found = false;
            var selected = default(AmbientSpawnSelection);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                if (!AmbientActorMath.TrySelectSpawn(paths, riderPosition, minimumSpawnDistance, spawnRadius,
                        _random, 1, out var candidate, out var rejectedClose, out var rejectedRadius, out var rejectedInvalid))
                {
                    tooClose += rejectedClose; outsideRadius += rejectedRadius; invalidPath += rejectedInvalid;
                    continue;
                }
                var minimumCyclistClearance = kind == AmbientActorKind.Car ? 24f : 8f;
                if (cyclists.Any(rider => NVector3.Distance(candidate.Position, rider.Position) < minimumCyclistClearance))
                { tooClose++; continue; }
                var candidatePath = paths[candidate.PathIndex];
                var candidatePose = candidatePath.Sample(candidate.DistanceMeters);
                if (kind == AmbientActorKind.Car
                    && WouldSpawnIntoImmediatePlayerCatch(candidatePose.Position, candidatePose.Heading,
                        candidatePath.SpeedRange.MinimumMetersPerSecond, cyclists))
                { tooClose++; continue; }
                var candidateLength = kind == AmbientActorKind.Car
                    ? AmbientActorEnvelopeMath.CarLengthMeters : AmbientActorEnvelopeMath.PedestrianLengthMeters;
                var candidateWidth = kind == AmbientActorKind.Car
                    ? AmbientActorEnvelopeMath.CarWidthMeters : AmbientActorEnvelopeMath.PedestrianWidthMeters;
                var candidateHeight = kind == AmbientActorKind.Car
                    ? AmbientActorEnvelopeMath.CarHeightMeters : AmbientActorEnvelopeMath.PedestrianHeightMeters;
                if (_active.Any(actor =>
                {
                    var obstacle = ToTrafficRider(actor);
                    return AmbientActorEnvelopeMath.Overlaps(candidatePose.Position, candidatePose.Heading,
                        candidateLength, candidateWidth, candidateHeight, obstacle.PoseAt(0),
                        obstacle.LengthMeters, obstacle.WidthMeters, obstacle.HeightMeters);
                }))
                { tooClose++; continue; }
                selected = candidate;
                found = true;
                break;
            }
            RecordRejections(kind, tooClose, outsideRadius, invalidPath, success: found);
            if (!found) break;
            var path = paths[selected.PathIndex];
            var speed = path.SpeedRange.MinimumMetersPerSecond
                + (float)_random.NextDouble() * (path.SpeedRange.MaximumMetersPerSecond - path.SpeedRange.MinimumMetersPerSecond);
            if (kind == AmbientActorKind.Pedestrian)
                speed = AmbientActorMath.PedestrianMinimumSpeedMetersPerSecond
                    + (float)_random.NextDouble() * (AmbientActorMath.PedestrianMaximumSpeedMetersPerSecond - AmbientActorMath.PedestrianMinimumSpeedMetersPerSecond);
            var (position, heading) = path.Sample(selected.DistanceMeters);
            if (!AmbientActorMath.IsValidTransform(position, heading))
            { if (kind == AmbientActorKind.Car) CarInvalidTransform++; else PedestrianInvalidTransform++; continue; }
            if (!AmbientActorMath.IsVisible(position, riderPosition, despawnRadius)) continue;
            var state = new AmbientActorState(_nextActorId++, kind, position, heading, speed, true);
            var visual = CreateVisual(kind);
            AddChild(visual);
            var actor = new ActiveActor { State = state, Path = path, DistanceAlongPath = selected.DistanceMeters,
                CruiseSpeedMetersPerSecond = speed, Visual = visual, LaneOffsetMeters = path.BaseLaneOffsetMeters };
            _active.Add(actor); ApplyPose(actor); activeCount++;
            GameLogger.Log("ambient_actor_spawned", new
            {
                actorId = state.ActorId, kind = kind.ToString(), sourceOsmId = path.SourceId,
                sourceName = path.SourceName, roadClass = path.SourceClass, direction = path.Direction,
                spawnPathDistanceMeters = selected.DistanceMeters, pathLengthMeters = path.LengthMeters,
                pathStart = AmbientActorDiagnosticPoint.From(path.Points[0]),
                pathEnd = AmbientActorDiagnosticPoint.From(path.Points[^1]),
                routeLoopContinuation = path.ContinuesAroundRoute,
                routeDistanceMeters = path.RouteConnectionDistanceMeters,
                remainingToEndpointMeters = path.LengthMeters - selected.DistanceMeters,
                spawnPosition = AmbientActorDiagnosticPoint.From(state.WorldPosition)
            });
        }
    }

    // Internal deterministic fixture seam used only by the Godot scene regression.
    internal void ConfigureTrafficTestFixture(AmbientActorPath path, double distanceAlongPathMeters, float speedMetersPerSecond)
    {
        _configured = true;
        AddTrafficTestFixture(AmbientActorKind.Car, path, distanceAlongPathMeters, speedMetersPerSecond);
    }

    internal void AddTrafficTestFixture(AmbientActorKind kind, AmbientActorPath path,
        double distanceAlongPathMeters, float speedMetersPerSecond)
    {
        var (position, heading) = path.Sample(distanceAlongPathMeters);
        var state = new AmbientActorState(_nextActorId++, kind, position, heading, speedMetersPerSecond, true);
        var visual = CreateVisual(kind);
        AddChild(visual);
        var actor = new ActiveActor
        {
            State = state,
            Path = path,
            DistanceAlongPath = distanceAlongPathMeters,
            CruiseSpeedMetersPerSecond = speedMetersPerSecond,
            Visual = visual,
            LaneOffsetMeters = path.BaseLaneOffsetMeters
        };
        _active.Add(actor);
        ApplyPose(actor);
    }

    internal void UpdateTrafficTestFixture(IReadOnlyList<AmbientTrafficRider> cyclists, double deltaSeconds) =>
        UpdateForCyclists(cyclists, deltaSeconds, paused: false);

    private Node3D CreateVisual(AmbientActorKind kind)
    {
        if (kind == AmbientActorKind.Car)
        {
            var car = new AmbientCarVisual { Name = $"AmbientCar{_nextActorId:D2}" };
            car.Build(_random); return car;
        }
        var pedestrian = new AmbientPedestrianVisual { Name = $"AmbientPedestrian{_nextActorId:D2}" };
        pedestrian.Build(_random); return pedestrian;
    }

    private void ApplyPose(ActiveActor actor)
    {
        switch (actor.Visual)
        {
            case AmbientCarVisual car:
                car.SetPose(actor.State.WorldPosition, actor.State.Heading);
                break;
            case AmbientPedestrianVisual pedestrian:
                pedestrian.SetPose(actor.State.WorldPosition, actor.State.Heading);
                break;
        }
    }

    private void RecordRejections(AmbientActorKind kind, int tooClose, int outsideRadius, int invalidPath, bool success)
    {
        var attempts = tooClose + outsideRadius + invalidPath + (success ? 1 : 0);
        if (kind == AmbientActorKind.Car)
        {
            CarSpawnAttempts += attempts; CarTooCloseToRider += tooClose;
            CarOutsideSpawnRadius += outsideRadius; InvalidOrShortCarPaths += invalidPath;
        }
        else
        {
            PedestrianSpawnAttempts += attempts; PedestrianTooCloseToRider += tooClose;
            PedestrianOutsideSpawnRadius += outsideRadius; InvalidOrShortPedestrianPaths += invalidPath;
        }
    }

    private bool FirstVisualVisible(AmbientActorKind kind)
    {
        var actor = _active.FirstOrDefault(item => item.State.Kind == kind);
        return actor is not null && GodotObject.IsInstanceValid(actor.Visual) && actor.Visual.Visible;
    }

    private static NVector3 ToNumerics(Godot.Vector3 value) => new(value.X, value.Y, value.Z);
    private static NVector3 ToNumerics(NVector3 value) => value;
    private static bool Finite(NVector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

public static class AmbientActorTuning
{
    public const int DefaultSeed = 84721;
    public const int TargetCars = 15;
    public const int MaximumCars = 20;
    public const int TargetPedestrians = 10;
    public const int MaximumPedestrians = 15;
    public const float CarMinimumSpawnDistanceMeters = 100;
    public const float CarSpawnRadiusMeters = 300;
    public const float CarDespawnRadiusMeters = 450;
    public const float PedestrianMinimumSpawnDistanceMeters = 35;
    public const float PedestrianSpawnRadiusMeters = 160;
    public const float PedestrianDespawnRadiusMeters = 220;
}
