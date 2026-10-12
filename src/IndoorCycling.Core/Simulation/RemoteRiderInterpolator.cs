namespace IndoorCycling.Core.Simulation;

/// <summary>Small time-buffered interpolation helper for absolute remote route progress.</summary>
public sealed class RemoteRiderInterpolator
{
    public const double InterpolationDelaySeconds = 0.20;
    public const double MaximumExtrapolationSeconds = 0.25;
    public const double StaleAfterSeconds = 2.0;
    private readonly List<RemoteRiderSample> _samples = [];
    public long LastSequence => _samples.Count == 0 ? -1 : _samples[^1].Sequence;
    public double LastReceivedAtSeconds => _samples.Count == 0 ? double.NegativeInfinity : _samples[^1].ReceivedAtSeconds;

    public bool Add(RemoteRiderSample sample)
    {
        if (!double.IsFinite(sample.DistanceMeters) || sample.DistanceMeters < 0 ||
            !double.IsFinite(sample.SpeedKph) || sample.SpeedKph < 0 || !double.IsFinite(sample.ReceivedAtSeconds) ||
            sample.Sequence <= LastSequence) return false;
        _samples.Add(sample);
        if (_samples.Count > 8) _samples.RemoveAt(0);
        return true;
    }

    public void Reset(RemoteRiderSample sample)
    {
        _samples.Clear();
        Add(sample);
    }

    public bool TrySample(double nowSeconds, out RemoteRiderSample sample)
    {
        sample = default;
        if (_samples.Count == 0 || nowSeconds - LastReceivedAtSeconds > StaleAfterSeconds) return false;
        var renderTime = nowSeconds - InterpolationDelaySeconds;
        var first = _samples[0];
        if (renderTime <= first.ReceivedAtSeconds) { sample = first; return true; }
        for (var i = 1; i < _samples.Count; i++)
        {
            var next = _samples[i];
            if (renderTime > next.ReceivedAtSeconds) continue;
            var previous = _samples[i - 1];
            var span = Math.Max(.001, next.ReceivedAtSeconds - previous.ReceivedAtSeconds);
            var t = Math.Clamp((renderTime - previous.ReceivedAtSeconds) / span, 0, 1);
            sample = Lerp(previous, next, t);
            return true;
        }
        var latest = _samples[^1];
        var extrapolation = Math.Clamp(renderTime - latest.ReceivedAtSeconds, 0, MaximumExtrapolationSeconds);
        sample = latest with { DistanceMeters = latest.DistanceMeters + (latest.Paused ? 0 : latest.SpeedKph / 3.6 * extrapolation) };
        return true;
    }

    private static RemoteRiderSample Lerp(RemoteRiderSample a, RemoteRiderSample b, double t) => b with
    {
        DistanceMeters = a.DistanceMeters + (b.DistanceMeters - a.DistanceMeters) * t,
        SpeedKph = a.SpeedKph + (b.SpeedKph - a.SpeedKph) * t,
        CadenceRpm = (int)Math.Round(a.CadenceRpm + (b.CadenceRpm - a.CadenceRpm) * t),
        PowerWatts = (int)Math.Round(a.PowerWatts + (b.PowerWatts - a.PowerWatts) * t),
        Paused = t < .5 ? a.Paused : b.Paused
    };
}

public readonly record struct RemoteRiderSample(string RiderId, string DisplayName, double DistanceMeters,
    double SpeedKph, int CadenceRpm, int PowerWatts, bool Paused, long Sequence, double ReceivedAtSeconds);

public static class MultiplayerRoutePosition
{
    public static double PositionBehind(double targetAbsoluteDistanceMeters, double behindMeters = 4)
    {
        if (!double.IsFinite(targetAbsoluteDistanceMeters) || targetAbsoluteDistanceMeters < 0 ||
            !double.IsFinite(behindMeters) || behindMeters < 0)
            throw new ArgumentOutOfRangeException(nameof(targetAbsoluteDistanceMeters));
        return Math.Max(0, targetAbsoluteDistanceMeters - behindMeters);
    }

    public static double WrappedSeparation(double firstAbsoluteDistanceMeters, double secondAbsoluteDistanceMeters,
        double routeLengthMeters)
    {
        if (routeLengthMeters <= 0 || !double.IsFinite(routeLengthMeters)) throw new ArgumentOutOfRangeException(nameof(routeLengthMeters));
        var first = ((firstAbsoluteDistanceMeters % routeLengthMeters) + routeLengthMeters) % routeLengthMeters;
        var second = ((secondAbsoluteDistanceMeters % routeLengthMeters) + routeLengthMeters) % routeLengthMeters;
        var gap = Math.Abs(first - second);
        return Math.Min(gap, routeLengthMeters - gap);
    }
}

/// <summary>Chooses a safe initial local speed from a fresh remote rider sample.</summary>
public static class CatchUpSpeedResolver
{
    public const double MaximumCatchUpSpeedKph = 80;

    public static double Resolve(double remoteSpeedKph, double sampleAgeSeconds, bool targetPaused)
    {
        if (targetPaused || !double.IsFinite(sampleAgeSeconds) || sampleAgeSeconds < 0 ||
            sampleAgeSeconds > RemoteRiderInterpolator.StaleAfterSeconds ||
            !double.IsFinite(remoteSpeedKph) || remoteSpeedKph < 0 || remoteSpeedKph > MaximumCatchUpSpeedKph)
            return 0;
        return remoteSpeedKph;
    }
}
