using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;

namespace IndoorCycling.Core.Simulation;

public enum CollectibleKind { BlueCat, GoldenCat }

public static class GameFeaturePreferencePolicy
{
	public static bool IsEnabled(IReadOnlyDictionary<string, bool> profiles, Guid? profileId) =>
		profileId is { } id && profiles.TryGetValue(id.ToString("D"), out var enabled) && enabled;
	public static void Set(IDictionary<string, bool> profiles, Guid profileId, bool enabled) =>
		profiles[profileId.ToString("D")] = enabled;
}

public sealed record CollectiblePlacement(string Id, double RouteDistanceMeters, double LateralMeters, CollectibleKind Kind, int Points);

public sealed record CollectibleChallengeResult(int SchemaVersion, string RouteId, string RouteContentIdentity,
	int ChallengeVersion, string AttemptId, int LapNumber, int Score, int PickupsCollected, int PickupsAvailable,
	bool Completed, double ElapsedSeconds, bool EligibleForPersonalBest, bool DummyRider, bool DebugJump,
	bool CatchUpUsed, bool GameFeatureToggledDuringRide, DateTimeOffset SavedAtUtc)
{
	public int FeatureLinesCompleted { get; init; }
}

/// <summary>Per-attempt identity, scoring and practice eligibility. Local flags are not anti-cheat validation.</summary>
public sealed class CollectibleAttemptState
{
	private readonly HashSet<string> _awarded = new(StringComparer.Ordinal);
	private readonly HashSet<string> _awardedFeatures = new(StringComparer.Ordinal);
	public Guid AttemptId { get; } = Guid.NewGuid();
	public int Score { get; private set; }
	public int PickupsCollected => _awarded.Count;
	public int FeatureLinesCompleted => _awardedFeatures.Count;
	public bool DummyRider { get; private set; }
	public bool DebugJump { get; private set; }
	public bool CatchUpUsed { get; private set; }
	public bool GameFeatureToggledDuringRide { get; private set; }
	public bool Eligible => !DummyRider && !DebugJump && !CatchUpUsed && !GameFeatureToggledDuringRide;
	public bool Award(CollectiblePlacement pickup, int lapNumber) => _awarded.Add($"{lapNumber}:{pickup.Id}") && Add(pickup.Points);
	public bool AwardFeatureLine(string featureId, int lapNumber, int points)
	{
		if (string.IsNullOrWhiteSpace(featureId) || points < 0 || points > 500
			|| !_awardedFeatures.Add($"{lapNumber}:{featureId}")) return false;
		return Add(points);
	}
	private bool Add(int points) { Score += points; return true; }
	public void MarkPractice(bool dummy = false, bool debugJump = false, bool catchUp = false, bool toggled = false)
	{ DummyRider |= dummy; DebugJump |= debugJump; CatchUpUsed |= catchUp; GameFeatureToggledDuringRide |= toggled; }
	public CollectibleChallengeResult CreateResult(string routeId, string contentIdentity, int challengeVersion,
		int lap, int available, bool completed, double elapsedSeconds) => new(1, routeId, contentIdentity,
		challengeVersion, AttemptId.ToString("D"), lap, Score, PickupsCollected, available, completed,
		Math.Max(0, elapsedSeconds), Eligible && completed, DummyRider, DebugJump, CatchUpUsed,
		GameFeatureToggledDuringRide, DateTimeOffset.UtcNow) { FeatureLinesCompleted = FeatureLinesCompleted };
}

/// <summary>Deterministic, route relative pickup layout and swept hit detection.</summary>
public static class CollectibleChallenge
{
    public const int CurrentVersion = 1;
    public static IReadOnlyList<CollectiblePlacement> Generate(string routeId, double routeLengthMeters,
        IReadOnlyList<(double Distance, System.Numerics.Vector3 Forward)> samples, int version = CurrentVersion)
    {
        if (string.IsNullOrWhiteSpace(routeId) || !double.IsFinite(routeLengthMeters) || routeLengthMeters < 200)
            return [];
        var seedBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{routeId}|{version}"));
        var random = new StableRandom(BinaryPrimitives.ReadUInt32LittleEndian(seedBytes));
        var result = new List<CollectiblePlacement>();
        var orderedSamples = samples.OrderBy(s => s.Distance).ToArray();
        // Leave starts and route closure clear; wide spacing gives riders time to steer.
        for (var distance = 90d; distance < routeLengthMeters - 90; distance += 180)
        {
            var turnNearPickup = orderedSamples.Skip(1).Select((sample, index) => (sample, previous: orderedSamples[index]))
                .Any(pair => Math.Abs(pair.sample.Distance - distance) <= 120 &&
                    System.Numerics.Vector3.Dot(pair.previous.Forward, pair.sample.Forward) < 0.72f);
            var forward = orderedSamples.OrderBy(s => Math.Abs(s.Distance - distance)).Take(2).ToArray();
            if (turnNearPickup || forward.Length < 2 || System.Numerics.Vector3.Dot(forward[0].Forward, forward[1].Forward) < 0.72f)
                continue;
            var lateral = (random.NextDouble() * 2 - 1) * 1.25;
            var gold = random.Next(8) == 0;
            result.Add(new($"v{version}:{routeId}:{distance:0}", distance, lateral,
                gold ? CollectibleKind.GoldenCat : CollectibleKind.BlueCat, gold ? 5 : 1));
        }
        return result;
    }

    public static bool SweptHit(double previousDistance, double currentDistance, double previousLateral,
        double currentLateral, CollectiblePlacement pickup, double routeLength, bool looping,
        double lateralRadius = 0.95)
    {
        if (!double.IsFinite(previousDistance) || !double.IsFinite(currentDistance) || routeLength <= 0 || lateralRadius <= 0)
            return false;
        var firstLap = (long)Math.Floor(previousDistance / routeLength);
        var lastLap = (long)Math.Floor(currentDistance / routeLength);
        if (lastLap < firstLap || lastLap - firstLap > 1) return false; // reverse motion / debug jumps
        for (var lap = firstLap; lap <= lastLap; lap++)
        {
            if (!looping && lap != 0) continue;
            var target = lap * routeLength + pickup.RouteDistanceMeters;
            if (target < previousDistance || target > currentDistance) continue;
            var t = currentDistance == previousDistance ? 0 : (target - previousDistance) / (currentDistance - previousDistance);
            var lateral = previousLateral + (currentLateral - previousLateral) * t;
            if (Math.Abs(lateral - pickup.LateralMeters) <= lateralRadius) return true;
        }
        return false;
    }

    private struct StableRandom(uint state)
    {
        private uint _state = state == 0 ? 0x9E3779B9u : state;
        private uint NextUInt()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state;
        }
        public double NextDouble() => (NextUInt() >> 8) / 16777216d;
        public int Next(int exclusiveMaximum) => (int)(NextUInt() % (uint)exclusiveMaximum);
    }
}
