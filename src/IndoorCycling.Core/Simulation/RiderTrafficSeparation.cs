using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Simulation;

/// <summary>Shared road-space rules for local human, AI and interpolated rider envelopes.</summary>
public static class RiderTrafficSeparation
{
    public const double RiderLengthMeters = 1.9;
    public const double RiderHalfWidthMeters = 0.43;
    public const double SideClearanceMeters = 0.14;
    public const double MinimumSameLaneGapMeters = RiderLengthMeters + 0.55;
    public const double FollowingHeadwaySeconds = 1.0;
    public const double MaximumBrakingMetersPerSecondSquared = 3.2;
    public const double MaximumAccelerationMetersPerSecondSquared = 1.5;
    public const double MaximumLateralSpeedMetersPerSecond = 0.75;
    public const double MaximumRoadCentreOffsetMeters = 2.75;
    public const double LookAheadSeconds = 2.5;
    public const double MinimumLaneCentreSeparationMeters = RiderHalfWidthMeters * 2 + SideClearanceMeters;

    private static readonly double[] LaneCentres = [-2.55, -1.53, -0.51, 0.51, 1.53, 2.55];

    /// <summary>
    /// Finds an AI rider's safe speed cap for the closest same-lane rider ahead. Route distance
    /// makes this independent of bends; wrapped distance handles lap seams without world-space shortcuts.
    /// </summary>
    public static double FollowingSpeedCap(RiderState follower, IReadOnlyList<RiderState> riders,
        double routeLengthMeters)
    {
        var ownSpeed = Math.Max(0, follower.VirtualSpeedMps);
        var cap = double.PositiveInfinity;
        foreach (var leader in riders)
        {
            if (leader.RiderId == follower.RiderId || leader.RouteId != follower.RouteId) continue;
            if (Math.Abs(leader.LateralOffsetMeters - follower.LateralOffsetMeters) >= MinimumLaneCentreSeparationMeters) continue;
            var ahead = AiRiderPopulation.RelativeDistance(leader.WrappedRouteDistanceMeters,
                follower.WrappedRouteDistanceMeters, routeLengthMeters);
            if (ahead <= 0 || ahead > 80) continue;
            var closing = ownSpeed - Math.Max(0, leader.VirtualSpeedMps);
            if (closing <= 0 && ahead > MinimumSameLaneGapMeters + 1) continue;
            var freeGap = Math.Max(0, ahead - MinimumSameLaneGapMeters);
            var leaderSpeed = Math.Max(0, leader.VirtualSpeedMps);
            var headwayCap = leaderSpeed + freeGap / FollowingHeadwaySeconds;
            var brakingCap = Math.Sqrt(leaderSpeed * leaderSpeed
                + 2 * MaximumBrakingMetersPerSecondSquared * freeGap);
            cap = Math.Min(cap, Math.Min(headwayCap, brakingCap));
        }
        return cap;
    }

    /// <summary>
    /// Assigns local AI riders to legal, physically distinct lane centres when a close pass is
    /// predicted. Local and remote riders keep their authoritative lane; ghosts are ignored.
    /// </summary>
    public static IReadOnlyDictionary<string, double> ResolveAiLaneTargets(
        IReadOnlyList<RiderState> riders, double routeLengthMeters)
    {
        var targets = riders.Where(rider => rider.RiderId != "ghost")
            .ToDictionary(rider => rider.RiderId, rider => Math.Clamp(rider.LateralOffsetMeters,
                -MaximumRoadCentreOffsetMeters, MaximumRoadCentreOffsetMeters), StringComparer.Ordinal);
        var fixedRiders = riders.Where(rider => rider.RiderId == "player"
            || (!rider.RiderId.StartsWith("ai-", StringComparison.Ordinal) && rider.RiderId != "ghost"))
            .ToList();
        var aiRiders = riders.Where(rider => rider.RiderId.StartsWith("ai-", StringComparison.Ordinal))
            .OrderBy(rider => rider.RouteDistanceMeters).ThenBy(rider => rider.RiderId, StringComparer.Ordinal).ToArray();
        var processedAi = new List<RiderState>();
        foreach (var rider in aiRiders)
        {
            var near = fixedRiders.Concat(processedAi)
                .Where(other => other.RouteId == rider.RouteId && WillShareLongitudinalSpace(rider, other, routeLengthMeters))
                .ToArray();
            if (near.Length == 0) { processedAi.Add(rider); continue; }

            var current = targets[rider.RiderId];
            var candidate = LaneCentres
                .Where(value => Math.Abs(value) <= MaximumRoadCentreOffsetMeters)
                .Select(value => new
                {
                    Value = value,
                    Conflicts = near.Count(other => Math.Abs(value - targets.GetValueOrDefault(other.RiderId,
                        other.LateralOffsetMeters)) < MinimumLaneCentreSeparationMeters),
                    Cost = Math.Abs(value - current) + Math.Abs(value - rider.LateralOffsetMeters) * 0.2
                })
                .OrderBy(item => item.Conflicts).ThenBy(item => item.Cost).ThenBy(item => item.Value)
                .FirstOrDefault();
            if (candidate is not null && candidate.Conflicts == 0)
                targets[rider.RiderId] = candidate.Value;
            processedAi.Add(rider);
        }
        return targets;
    }

    /// <summary>
    /// Computes a deterministic local presentation lane for a remote rider near an occupied
    /// envelope. This never changes the remote RiderState, network messages, route distance or
    /// telemetry; callers must smooth the visual offset and retain network authority.
    /// </summary>
    public static double RemotePresentationLaneTarget(RiderState remote,
        IReadOnlyList<RiderState> riders, double routeLengthMeters)
    {
        var neighbors = riders.Where(other => other.RiderId != remote.RiderId && other.RiderId != "ghost"
                && other.RouteId == remote.RouteId && WillShareLongitudinalSpace(remote, other, routeLengthMeters))
            .ToArray();
        if (neighbors.Length == 0) return Math.Clamp(remote.LateralOffsetMeters,
            -MaximumRoadCentreOffsetMeters, MaximumRoadCentreOffsetMeters);
        var candidates = LaneCentres.Where(value => Math.Abs(value) <= MaximumRoadCentreOffsetMeters)
            .Select(value => new
            {
                Value = value,
                Conflicts = neighbors.Count(other => Math.Abs(value - other.LateralOffsetMeters)
                    < MinimumLaneCentreSeparationMeters),
                Cost = Math.Abs(value - remote.LateralOffsetMeters)
            })
            .OrderBy(item => item.Conflicts).ThenBy(item => item.Cost).ThenBy(item => item.Value).ToArray();
        return candidates.FirstOrDefault(item => item.Conflicts == 0)?.Value ?? candidates[0].Value;
    }

    public static bool HasModelEnvelopeOverlap(RiderState first, RiderState second,
        double routeLengthMeters, double longitudinalToleranceMeters = 0.02)
    {
        if (first.RouteId != second.RouteId || first.RiderId == "ghost" || second.RiderId == "ghost") return false;
        var longitudinal = Math.Abs(AiRiderPopulation.RelativeDistance(first.WrappedRouteDistanceMeters,
            second.WrappedRouteDistanceMeters, routeLengthMeters));
        var lateral = Math.Abs(first.LateralOffsetMeters - second.LateralOffsetMeters);
        return longitudinal < RiderLengthMeters - longitudinalToleranceMeters
            && lateral < RiderHalfWidthMeters * 2 - longitudinalToleranceMeters;
    }

    private static bool WillShareLongitudinalSpace(RiderState first, RiderState second, double routeLengthMeters)
    {
        if (first.RiderId == "ghost" || second.RiderId == "ghost") return false;
        var relative = AiRiderPopulation.RelativeDistance(second.WrappedRouteDistanceMeters,
            first.WrappedRouteDistanceMeters, routeLengthMeters);
        var relativeSpeed = Math.Max(0, second.VirtualSpeedMps) - Math.Max(0, first.VirtualSpeedMps);
        var end = Math.Clamp(LookAheadSeconds, 0, Math.Max(0.25, routeLengthMeters / 4));
        var closest = Math.Min(Math.Abs(relative), Math.Abs(relative + relativeSpeed * end));
        if (relativeSpeed != 0)
        {
            var time = Math.Clamp(-relative / relativeSpeed, 0, end);
            closest = Math.Min(closest, Math.Abs(relative + relativeSpeed * time));
        }
        return closest < MinimumSameLaneGapMeters + 0.5;
    }
}
