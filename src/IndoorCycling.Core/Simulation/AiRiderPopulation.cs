using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Simulation;

public sealed record AiRiderProfile(string DisplayName, int TargetPowerWatts, int CadenceRpm,
    double TotalMassKg, double RollingResistanceCoefficient, double DragCoefficientArea,
    double InitialRelativeDistanceMeters, double LateralOffsetMeters, double InitialSpeedMetersPerSecond);

/// <summary>One deterministic local AI rider with private distance and speed state.</summary>
public sealed class AiRiderController
{
    private double _routeDistanceMeters;
    private double _speedMetersPerSecond;
    private double _simulationTimeSeconds;
    private string _routeId = string.Empty;
    private int _lapNumber = 1;
    private double _wrappedDistanceMeters;
    private double _lateralOffsetMeters;
    private double _targetLateralOffsetMeters;
    private bool _paused;
    private bool _initialized;

    public AiRiderProfile Profile { get; }
    public string RiderId => $"ai-{Profile.DisplayName.ToLowerInvariant().Replace(' ', '-')}";
    public RiderState State => new(RiderId,
        Profile.DisplayName, _routeId, _routeDistanceMeters, _wrappedDistanceMeters, _lapNumber,
        _lateralOffsetMeters, _speedMetersPerSecond, Profile.TargetPowerWatts, Profile.CadenceRpm,
        _paused, _simulationTimeSeconds);

    public AiRiderController(AiRiderProfile profile) => Profile = profile;

    public void PlaceRelativeTo(RiderState player, double routeLengthMeters)
    {
        _routeId = player.RouteId;
        _routeDistanceMeters = player.RouteDistanceMeters + Profile.InitialRelativeDistanceMeters;
        _speedMetersPerSecond = Profile.InitialSpeedMetersPerSecond;
        _simulationTimeSeconds = player.SimulationTimeSeconds;
        _lateralOffsetMeters = Profile.LateralOffsetMeters;
        _targetLateralOffsetMeters = Profile.LateralOffsetMeters;
        _initialized = true;
        UpdateRouteCoordinates(routeLengthMeters);
    }

    public void RecycleRelativeTo(RiderState player, double routeLengthMeters)
    {
        _routeId = player.RouteId;
        _routeDistanceMeters = player.RouteDistanceMeters + Profile.InitialRelativeDistanceMeters;
        UpdateRouteCoordinates(routeLengthMeters);
    }

    public void SetLateralOffset(double lateralOffsetMeters)
    {
        if (double.IsFinite(lateralOffsetMeters))
            _targetLateralOffsetMeters = Math.Clamp(lateralOffsetMeters,
                -RiderTrafficSeparation.MaximumRoadCentreOffsetMeters,
                RiderTrafficSeparation.MaximumRoadCentreOffsetMeters);
    }

    public void Update(double routeLengthMeters, Func<double, double> gradientAt,
        double deltaSeconds, bool paused, SimulationPhysicsParameters? physics = null,
        double maximumSpeedMetersPerSecond = double.PositiveInfinity)
    {
        _paused = paused;
        if (!_initialized || paused || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0) return;
        var remaining = deltaSeconds;
        while (remaining > 0)
        {
            var stepSeconds = Math.Min(remaining, 0.25);
            var wrapped = WrapDistance(_routeDistanceMeters, routeLengthMeters);
            var step = CyclingPhysicsMath.Advance(_speedMetersPerSecond, Profile.TargetPowerWatts,
                gradientAt(wrapped), Profile.TotalMassKg, Profile.RollingResistanceCoefficient,
                Profile.DragCoefficientArea, stepSeconds, physics);
            var previousSpeed = _speedMetersPerSecond;
            var desiredSpeed = Math.Min(step.SpeedMetersPerSecond, maximumSpeedMetersPerSecond);
            var speedLimit = desiredSpeed < previousSpeed
                ? RiderTrafficSeparation.MaximumBrakingMetersPerSecondSquared * stepSeconds
                : RiderTrafficSeparation.MaximumAccelerationMetersPerSecondSquared * stepSeconds;
            _speedMetersPerSecond = previousSpeed + Math.Clamp(desiredSpeed - previousSpeed, -speedLimit, speedLimit);
            var constrained = _speedMetersPerSecond + 1e-6 < step.SpeedMetersPerSecond;
            _routeDistanceMeters += constrained
                ? (previousSpeed + _speedMetersPerSecond) * 0.5 * stepSeconds
                : step.DistanceMeters;
            var lateralStep = RiderTrafficSeparation.MaximumLateralSpeedMetersPerSecond * stepSeconds;
            _lateralOffsetMeters += Math.Clamp(_targetLateralOffsetMeters - _lateralOffsetMeters,
                -lateralStep, lateralStep);
            _simulationTimeSeconds += stepSeconds;
            remaining -= stepSeconds;
        }
        UpdateRouteCoordinates(routeLengthMeters);
    }

    private void UpdateRouteCoordinates(double routeLengthMeters)
    {
        _wrappedDistanceMeters = WrapDistance(_routeDistanceMeters, routeLengthMeters);
        _lapNumber = (int)Math.Floor(_routeDistanceMeters / routeLengthMeters) + 1;
    }

    internal static double WrapDistance(double distance, double length)
    {
        if (!double.IsFinite(distance) || !double.IsFinite(length) || length <= 0) return 0;
        return ((distance % length) + length) % length;
    }
}

/// <summary>Pure local AI population; it never reads or mutates player physics state.</summary>
public sealed class AiRiderPopulation : IRiderStateSource
{
    public const int DefaultTargetCount = 8;
    public const int DefaultMaximumCount = 12;
    public const int DefaultSeed = 61327;
    public const double RecycleDistanceMeters = 650;
    public const double OverlapDistanceMeters = 3;
    public const double MinimumSpawnSeparationMeters = 25;
    public const double MinimumLateralOffsetMeters = 1.2;
    public const double MaximumLateralOffsetMeters = 2.2;

    private static readonly double[] SpawnOffsets = [60, 120, 195, 285, -55, -115, -180, -245, 80, 155, -80, -210];
    private static readonly double[] OverlapSlots = [1.2, 1.32, 1.44, 1.56, 1.68, 1.84, 1.96, 2.08];
    private readonly List<AiRiderController> _controllers;
    private IReadOnlyList<RiderState> _riders = Array.Empty<RiderState>();
    private bool _initialized;

    public int TargetCount { get; }
    public int MaximumCount { get; }
    public IReadOnlyList<AiRiderProfile> Profiles { get; }
    public IReadOnlyList<AiRiderController> Controllers => _controllers;

    public AiRiderPopulation(int targetCount = DefaultTargetCount,
        int maximumCount = DefaultMaximumCount, int seed = DefaultSeed)
    {
        if (targetCount < 0 || maximumCount < targetCount || maximumCount > DefaultMaximumCount)
            throw new ArgumentOutOfRangeException(nameof(targetCount));
        TargetCount = targetCount;
        MaximumCount = maximumCount;
        Profiles = CreateProfiles(Math.Min(targetCount, maximumCount), seed);
        _controllers = Profiles.Select(profile => new AiRiderController(profile)).ToList();
    }

    public void Update(RiderState player, double routeLengthMeters,
        Func<double, double> gradientAt, double deltaSeconds, IReadOnlyList<RiderState>? externalRiders = null)
    {
        if (!double.IsFinite(routeLengthMeters) || routeLengthMeters <= 0)
        { _riders = [player]; return; }
        if (!_initialized)
        {
            foreach (var controller in _controllers)
            {
                if (Math.Abs(controller.Profile.InitialRelativeDistanceMeters) < MinimumSpawnSeparationMeters)
                    continue;
                controller.PlaceRelativeTo(player, routeLengthMeters);
            }
            _initialized = true;
        }
        var external = externalRiders ?? Array.Empty<RiderState>();
        var trafficSnapshot = new[] { player }.Concat(_controllers.Select(controller => controller.State))
            .Concat(external).Where(rider => rider.RiderId != "ghost" && rider.RouteId == player.RouteId).ToArray();
        var laneTargets = RiderTrafficSeparation.ResolveAiLaneTargets(trafficSnapshot, routeLengthMeters);
        foreach (var controller in _controllers)
        {
            var state = controller.State;
            var speedCap = RiderTrafficSeparation.FollowingSpeedCap(state, trafficSnapshot, routeLengthMeters);
            if (laneTargets.TryGetValue(state.RiderId, out var targetOffset))
                controller.SetLateralOffset(targetOffset);
            var relative = RelativeDistance(controller.State.WrappedRouteDistanceMeters,
                player.WrappedRouteDistanceMeters, routeLengthMeters);
            if (Math.Abs(relative) > RecycleDistanceMeters && !player.Paused)
                controller.RecycleRelativeTo(player, routeLengthMeters);
            controller.Update(routeLengthMeters, gradientAt, deltaSeconds, player.Paused,
                maximumSpeedMetersPerSecond: speedCap);
        }
        _riders = new[] { player }.Concat(_controllers.Select(controller => controller.State)).ToArray();
    }

    public IReadOnlyList<RiderState> GetRiders() => _riders;

    public static double RelativeDistance(double riderWrappedDistance, double referenceWrappedDistance,
        double routeLengthMeters)
    {
        if (!double.IsFinite(routeLengthMeters) || routeLengthMeters <= 0) return 0;
        var delta = riderWrappedDistance - referenceWrappedDistance;
        return ((delta + routeLengthMeters / 2) % routeLengthMeters + routeLengthMeters) % routeLengthMeters
            - routeLengthMeters / 2;
    }

    public static IReadOnlyDictionary<string, double> ResolveVisualOverlapOffsets(
        IReadOnlyList<RiderState> riders, double routeLengthMeters) =>
        RiderTrafficSeparation.ResolveAiLaneTargets(riders, routeLengthMeters);

    public static IReadOnlyList<AiRiderProfile> CreateProfiles(int count, int seed = DefaultSeed)
    {
        count = Math.Clamp(count, 0, DefaultMaximumCount);
        string[] names = ["Alex", "Jamie", "Sam", "Taylor", "Morgan", "Casey", "Jordan", "Riley", "Avery", "Cameron", "Drew", "Robin"];
        (int Min, int Max)[] powerRanges = [(120, 160), (170, 220), (230, 280)];
        var random = new Random(seed);
        var profiles = new List<AiRiderProfile>(count);
        for (var i = 0; i < count; i++)
        {
            var range = powerRanges[Math.Min(2, i * 3 / Math.Max(1, count))];
            var offset = SpawnOffsets[i % SpawnOffsets.Length];
            if (Math.Abs(offset) < MinimumSpawnSeparationMeters) offset = (i % 2 == 0 ? 1 : -1) * (50 + i * 10);
            var lateral = 1.2 + random.NextDouble();
            profiles.Add(new AiRiderProfile(names[i], random.Next(range.Min, range.Max + 1),
                random.Next(70, 96), 65 + random.Next(0, 26), 0.005, 0.32,
                offset, lateral, 7 + random.NextDouble() * 2));
        }
        return profiles;
    }

}

/// <summary>Combines the player snapshot with the local AI source for source-agnostic rendering.</summary>
public sealed class LocalRiderStateSource : IRiderStateSource
{
    private readonly AiRiderPopulation _aiPopulation;
    public LocalRiderStateSource(AiRiderPopulation aiPopulation) => _aiPopulation = aiPopulation;
    public void Update(RiderState player, double routeLengthMeters, Func<double, double> gradientAt, double deltaSeconds) =>
        _aiPopulation.Update(player, routeLengthMeters, gradientAt, deltaSeconds);
    public void Update(RiderState player, double routeLengthMeters, Func<double, double> gradientAt,
        double deltaSeconds, IReadOnlyList<RiderState> externalRiders) =>
        _aiPopulation.Update(player, routeLengthMeters, gradientAt, deltaSeconds, externalRiders);
    public IReadOnlyList<RiderState> GetRiders() => _aiPopulation.GetRiders();
}
