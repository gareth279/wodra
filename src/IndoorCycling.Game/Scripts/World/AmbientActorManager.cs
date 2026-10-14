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
        public required Node3D Visual;
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
    public AmbientActorState? FirstPedestrianState => _active.FirstOrDefault(actor => actor.State.Kind == AmbientActorKind.Pedestrian)?.State;
    public string? FirstCarRoadClass => _active.FirstOrDefault(actor => actor.State.Kind == AmbientActorKind.Car)?.Path.SourceClass;
    public string? FirstCarRoadName => _active.FirstOrDefault(actor => actor.State.Kind == AmbientActorKind.Car)?.Path.SourceName;
    public bool FirstCarVisible => FirstVisualVisible(AmbientActorKind.Car);
    public bool FirstPedestrianVisible => FirstVisualVisible(AmbientActorKind.Pedestrian);
    public int ApproximateTriangleCount => ActiveCarCount * AmbientCarVisual.ApproximateTriangleCount
        + ActivePedestrianCount * AmbientPedestrianVisual.ApproximateTriangleCount;

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

    public void UpdateForRider(NVector3 riderWorldPosition, double deltaSeconds, bool paused)
    {
        if (!_configured || paused || !Finite(riderWorldPosition)) return;
        UpdateAndCull(_active, riderWorldPosition, deltaSeconds);
        EnsurePopulation(AmbientActorKind.Car, _carPaths, riderWorldPosition,
            AmbientActorTuning.TargetCars, AmbientActorTuning.MaximumCars,
            AmbientActorTuning.CarMinimumSpawnDistanceMeters, AmbientActorTuning.CarSpawnRadiusMeters,
            AmbientActorTuning.CarDespawnRadiusMeters);
        EnsurePopulation(AmbientActorKind.Pedestrian, _pedestrianPaths, riderWorldPosition,
            AmbientActorTuning.TargetPedestrians, AmbientActorTuning.MaximumPedestrians,
            AmbientActorTuning.PedestrianMinimumSpawnDistanceMeters, AmbientActorTuning.PedestrianSpawnRadiusMeters,
            AmbientActorTuning.PedestrianDespawnRadiusMeters);
    }

    private void UpdateAndCull(List<ActiveActor> actors, NVector3 riderPosition, double deltaSeconds)
    {
        for (var i = actors.Count - 1; i >= 0; i--)
        {
            var actor = actors[i];
            var nextDistance = AmbientActorMath.AdvancePathDistance(actor.Path, actor.DistanceAlongPath,
                actor.State.SpeedMetersPerSecond, deltaSeconds, paused: false);
            var nextState = AmbientActorMath.Advance(actor.State, actor.Path, actor.DistanceAlongPath, deltaSeconds, paused: false);
            var visible = AmbientActorMath.IsVisible(ToNumerics(nextState.WorldPosition), riderPosition,
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
                var distanceToRider = NVector3.Distance(ToNumerics(nextState.WorldPosition), riderPosition);
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

    private void EnsurePopulation(AmbientActorKind kind, IReadOnlyList<AmbientActorPath> paths,
        NVector3 riderPosition, int target, int maximum, float minimumSpawnDistance,
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
            if (!AmbientActorMath.TrySelectSpawn(paths, riderPosition, minimumSpawnDistance, spawnRadius,
                    _random, attempts, out var selected, out var tooClose, out var outsideRadius, out var invalidPath))
            {
                RecordRejections(kind, tooClose, outsideRadius, invalidPath, success: false);
                break;
            }
            RecordRejections(kind, tooClose, outsideRadius, invalidPath, success: true);
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
            var actor = new ActiveActor { State = state, Path = path, DistanceAlongPath = selected.DistanceMeters, Visual = visual };
            _active.Add(actor); ApplyPose(actor); activeCount++;
            GameLogger.Log("ambient_actor_spawned", new
            {
                actorId = state.ActorId, kind = kind.ToString(), sourceOsmId = path.SourceId,
                sourceName = path.SourceName, roadClass = path.SourceClass, direction = path.Direction,
                spawnPathDistanceMeters = selected.DistanceMeters, pathLengthMeters = path.LengthMeters,
                pathStart = path.Points[0], pathEnd = path.Points[^1],
                routeLoopContinuation = path.ContinuesAroundRoute,
                routeDistanceMeters = path.RouteConnectionDistanceMeters,
                remainingToEndpointMeters = path.LengthMeters - selected.DistanceMeters,
                spawnPosition = state.WorldPosition
            });
        }
    }

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
