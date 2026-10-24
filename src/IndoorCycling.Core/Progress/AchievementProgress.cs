namespace IndoorCycling.Core.Progress;

public enum AchievementMetric { DistanceMeters, RidingSeconds, ClimbingMeters, RouteCompletion, Collection }
public enum PersonalGoalMetric { DistanceMeters, ClimbingMeters }

public sealed record AchievementDefinition(string Id, string Title, string Description,
    AchievementMetric Metric, double? Threshold = null, string? RouteId = null);

public sealed record AchievementRideEvidence(bool EligibleForProgress, string EnvironmentId,
    int RouteDefinitionVersion, string ActivityModeId, bool RouteCompletionEligible, bool FullRouteCompleted);

public sealed record AchievementTotals(double DistanceMeters, double RidingSeconds, double ClimbingMeters);

public sealed record AchievementAwardRecord(string AchievementId, DateTimeOffset EarnedAtUtc, Guid? ActivityId);

public sealed record PersonalGoalRecord(Guid ProfileId, PersonalGoalMetric Metric, double TargetValue,
    DateTimeOffset CreatedAtUtc, double ProgressValue)
{
    public double ProgressRatio => TargetValue <= 0 ? 0 : Math.Clamp(ProgressValue / TargetValue, 0, 1);
    public bool IsComplete => ProgressValue >= TargetValue;
}

public sealed record AchievementProgressSnapshot(Guid ProfileId, AchievementTotals Totals,
    IReadOnlyList<AchievementAwardRecord> Awards, PersonalGoalRecord? Goal);

public sealed record AchievementActivityResult(IReadOnlyList<AchievementDefinition> NewlyEarned,
    AchievementProgressSnapshot Snapshot);

/// <summary>Small, stable achievement catalogue. Collection membership is versioned content.</summary>
public static class AchievementCatalog
{
    public const int DunedinRoadCollectionVersion = 1;
    public const string DunedinRoadCollectionId = "collection.dunedin-road-routes.v1";

    public static IReadOnlyList<string> DunedinRoadRouteIds { get; } =
    [
        "dunedin-one-way-oval-gardens",
        "dunedin-south-coastal-loop",
        "dunedin-rolling-loop",
        "dunedin-signal-hill-climbing-loop"
    ];

    private static readonly AchievementDefinition[] Milestones =
    [
        new("milestone.distance.100km", "First 100 km", "Ride 100 km across eligible saved rides.", AchievementMetric.DistanceMeters, 100_000),
        new("milestone.distance.500km", "500 km", "Ride 500 km across eligible saved rides.", AchievementMetric.DistanceMeters, 500_000),
        new("milestone.distance.1000km", "1,000 km", "Ride 1,000 km across eligible saved rides.", AchievementMetric.DistanceMeters, 1_000_000),
        new("milestone.time.10h", "10 hours riding", "Spend 10 active hours riding.", AchievementMetric.RidingSeconds, 36_000),
        new("milestone.time.50h", "50 hours riding", "Spend 50 active hours riding.", AchievementMetric.RidingSeconds, 180_000),
        new("milestone.time.100h", "100 hours riding", "Spend 100 active hours riding.", AchievementMetric.RidingSeconds, 360_000),
        new("milestone.climbing.1km", "First 1,000 m climbing", "Climb 1,000 m across eligible saved rides.", AchievementMetric.ClimbingMeters, 1_000),
        new("milestone.climbing.5km", "5,000 m climbing", "Climb 5,000 m across eligible saved rides.", AchievementMetric.ClimbingMeters, 5_000),
        new("milestone.climbing.10km", "10,000 m climbing", "Climb 10,000 m across eligible saved rides.", AchievementMetric.ClimbingMeters, 10_000),
        new(DunedinRoadCollectionId, "Dunedin road collection", "Complete all four Dunedin road routes in collection v1.", AchievementMetric.Collection)
    ];

    public static string RouteCompletionId(string routeId) => $"route.first-complete.{routeId}";

    public static AchievementDefinition RouteCompletion(string routeId, string routeName) =>
        new(RouteCompletionId(routeId), $"{routeName} complete", $"Complete the full {routeName} route.",
            AchievementMetric.RouteCompletion, RouteId: routeId);

    public static IReadOnlyList<AchievementDefinition> MilestoneDefinitions => Milestones;

    public static IReadOnlyList<AchievementDefinition> Evaluate(AchievementTotals totals,
        IReadOnlyCollection<string> completedRouteIds, IReadOnlyCollection<string> eligibleRouteIds)
    {
        var result = new List<AchievementDefinition>();
        foreach (var routeId in completedRouteIds.Intersect(eligibleRouteIds, StringComparer.Ordinal))
            result.Add(RouteCompletion(routeId, routeId));

        foreach (var definition in Milestones.Where(item => item.Metric != AchievementMetric.Collection))
        {
            var value = definition.Metric switch
            {
                AchievementMetric.DistanceMeters => totals.DistanceMeters,
                AchievementMetric.RidingSeconds => totals.RidingSeconds,
                AchievementMetric.ClimbingMeters => totals.ClimbingMeters,
                _ => 0
            };
            if (definition.Threshold is { } threshold && value >= threshold) result.Add(definition);
        }

        if (DunedinRoadRouteIds.All(completedRouteIds.Contains))
            result.Add(Milestones.Single(item => item.Id == DunedinRoadCollectionId));
        return result;
    }

    public static IReadOnlyList<AchievementDefinition> DisplayDefinitions(IEnumerable<(string Id, string Name)> eligibleRoutes) =>
        eligibleRoutes.Select(route => RouteCompletion(route.Id, route.Name)).Concat(Milestones).ToArray();
}

/// <summary>Previews crossings during a live ride for optional toast display; persisted evaluation remains activity-ID based.</summary>
public sealed class AchievementRideNotificationTracker
{
    private readonly AchievementTotals _startingTotals;
    private readonly HashSet<string> _startingAwards;
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);

    public AchievementRideNotificationTracker(AchievementProgressSnapshot startingProgress)
    {
        _startingTotals = startingProgress.Totals;
        _startingAwards = startingProgress.Awards.Select(item => item.AchievementId).ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<AchievementDefinition> Observe(double distanceMeters, double ridingSeconds, double climbingMeters,
        string routeId, string routeName, string environmentId, bool routeCompletionEligible, bool fullRouteCompleted)
    {
        var totals = new AchievementTotals(
            _startingTotals.DistanceMeters + Math.Max(0, double.IsFinite(distanceMeters) ? distanceMeters : 0),
            _startingTotals.RidingSeconds + Math.Max(0, double.IsFinite(ridingSeconds) ? ridingSeconds : 0),
            _startingTotals.ClimbingMeters + Math.Max(0, double.IsFinite(climbingMeters) ? climbingMeters : 0));
        var completedRoutes = _startingAwards.Where(id => id.StartsWith("route.first-complete.", StringComparison.Ordinal))
            .Select(id => id["route.first-complete.".Length..]).ToHashSet(StringComparer.Ordinal);
        var eligibleRoutes = completedRoutes.ToHashSet(StringComparer.Ordinal);
        if (string.Equals(environmentId, "Road", StringComparison.OrdinalIgnoreCase) && routeCompletionEligible)
        {
            eligibleRoutes.Add(routeId);
            if (fullRouteCompleted) completedRoutes.Add(routeId);
        }
        var candidates = AchievementCatalog.Evaluate(totals, completedRoutes, eligibleRoutes);
        var result = new List<AchievementDefinition>();
        foreach (var candidate in candidates)
        {
            if (_startingAwards.Contains(candidate.Id) || !_notified.Add(candidate.Id)) continue;
            result.Add(candidate.Metric == AchievementMetric.RouteCompletion && candidate.RouteId == routeId
                ? AchievementCatalog.RouteCompletion(routeId, routeName) : candidate);
        }
        return result;
    }
}

/// <summary>Tracks continuous authoritative route movement; only an uninterrupted full lap can satisfy a route badge.</summary>
public sealed class RouteCompletionCoverageTracker
{
    private double _routeLengthMeters;
    private double _lastDistanceMeters;
    private double _distanceSinceCheckpointMeters;
    private long _startLapNumber;
    private bool _crossedLapBoundary;

    public bool CompletedFullRoute { get; private set; }

    public void Reset(double routeLengthMeters, double initialDistanceMeters = 0)
    {
        _routeLengthMeters = double.IsFinite(routeLengthMeters) && routeLengthMeters > 0 ? routeLengthMeters : 0;
        _lastDistanceMeters = double.IsFinite(initialDistanceMeters) && initialDistanceMeters >= 0 ? initialDistanceMeters : 0;
        _distanceSinceCheckpointMeters = 0;
        _startLapNumber = _routeLengthMeters > 0 ? (long)Math.Floor(_lastDistanceMeters / _routeLengthMeters) : 0;
        _crossedLapBoundary = false;
        CompletedFullRoute = false;
    }

    public void Observe(double distanceMeters, bool active)
    {
        if (!double.IsFinite(distanceMeters) || distanceMeters < 0) return;
        var delta = distanceMeters - _lastDistanceMeters;
        var previousLap = _routeLengthMeters > 0 ? (long)Math.Floor(_lastDistanceMeters / _routeLengthMeters) : 0;
        var currentLap = _routeLengthMeters > 0 ? (long)Math.Floor(distanceMeters / _routeLengthMeters) : 0;
        _lastDistanceMeters = distanceMeters;
        if (!active || delta <= 0 || _routeLengthMeters <= 0 || CompletedFullRoute) return;
        _distanceSinceCheckpointMeters += delta;
        if (currentLap > previousLap) _crossedLapBoundary = true;
        if (_crossedLapBoundary && currentLap > _startLapNumber &&
            _distanceSinceCheckpointMeters + 1e-6 >= _routeLengthMeters) CompletedFullRoute = true;
    }

    /// <summary>Call after a position jump; movement before and after the jump cannot combine into a full route.</summary>
    public void MarkDiscontinuity(double destinationDistanceMeters)
    {
        if (double.IsFinite(destinationDistanceMeters) && destinationDistanceMeters >= 0)
        {
            _lastDistanceMeters = destinationDistanceMeters;
            _startLapNumber = _routeLengthMeters > 0 ? (long)Math.Floor(_lastDistanceMeters / _routeLengthMeters) : 0;
        }
        _distanceSinceCheckpointMeters = 0;
        _crossedLapBoundary = false;
    }
}
