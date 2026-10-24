namespace IndoorCycling.Core.Models;

/// <summary>A compact, engine-independent playback trace extracted from recorded FIT records.</summary>
public sealed record GhostRideSample(double ElapsedSeconds, double DistanceMeters,
    double? SpeedMps, int? PowerWatts, int? CadenceRpm);

public sealed class GhostRideTrace
{
    public IReadOnlyList<GhostRideSample> Samples { get; }
    public double DurationSeconds => Samples.Count == 0 ? 0 : Samples[^1].ElapsedSeconds;
    public double TotalDistanceMeters => Samples.Count == 0 ? 0 : Samples[^1].DistanceMeters;

    public GhostRideTrace(IEnumerable<GhostRideSample> samples)
    {
        var valid = samples.Where(s => double.IsFinite(s.ElapsedSeconds) && s.ElapsedSeconds >= 0 &&
            double.IsFinite(s.DistanceMeters) && s.DistanceMeters >= 0).OrderBy(s => s.ElapsedSeconds).ToArray();
        if (valid.Length < 2) throw new ArgumentException("A ghost trace requires at least two valid samples.", nameof(samples));
        for (var i = 1; i < valid.Length; i++)
            if (valid[i].ElapsedSeconds <= valid[i - 1].ElapsedSeconds || valid[i].DistanceMeters < valid[i - 1].DistanceMeters - 2)
                throw new ArgumentException("Ghost samples must have increasing time and distance.", nameof(samples));
        Samples = valid;
    }

    public GhostRideSample SampleAt(double elapsedSeconds, double maximumGapSeconds = 10)
    {
        var t = Math.Clamp(double.IsFinite(elapsedSeconds) ? elapsedSeconds : 0, 0, DurationSeconds);
        if (t >= DurationSeconds) return Samples[^1];
        if (t <= Samples[0].ElapsedSeconds) return Samples[0];
        var hi = 1;
        while (hi < Samples.Count - 1 && Samples[hi].ElapsedSeconds < t) hi++;
        var a = Samples[hi - 1]; var b = Samples[hi];
        if (b.ElapsedSeconds - a.ElapsedSeconds > maximumGapSeconds) return a;
        var f = Math.Clamp((t - a.ElapsedSeconds) / (b.ElapsedSeconds - a.ElapsedSeconds), 0, 1);
        static double? Mix(double? x, double? y, double f) => x.HasValue && y.HasValue ? x + (y - x) * f : x ?? y;
        static int? MixInt(int? x, int? y, double f) => x.HasValue && y.HasValue ? (int)Math.Round(x.Value + (y.Value - x.Value) * f) : x ?? y;
        return new GhostRideSample(t, a.DistanceMeters + (b.DistanceMeters - a.DistanceMeters) * f,
            Mix(a.SpeedMps, b.SpeedMps, f), MixInt(a.PowerWatts, b.PowerWatts, f), MixInt(a.CadenceRpm, b.CadenceRpm, f));
    }

    /// <summary>Finds the first recorded elapsed time at which the rider reached an unwrapped distance.</summary>
    public bool TryGetElapsedAtDistance(double distanceMeters, out double elapsedSeconds, double maximumGapSeconds = 10)
    {
        elapsedSeconds = 0;
        if (!double.IsFinite(distanceMeters) || distanceMeters < Samples[0].DistanceMeters - 1e-6 ||
            distanceMeters > TotalDistanceMeters + 1e-6 || !double.IsFinite(maximumGapSeconds) || maximumGapSeconds <= 0)
            return false;
        if (distanceMeters <= Samples[0].DistanceMeters + 1e-6)
        { elapsedSeconds = Samples[0].ElapsedSeconds; return true; }

        // Lower-bound search intentionally selects the first sample on a stationary plateau: it is
        // the rider's arrival time at that progress, not time spent stopped after arrival.
        var lo = 0;
        var hi = Samples.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (Samples[mid].DistanceMeters + 1e-9 < distanceMeters) lo = mid + 1;
            else hi = mid;
        }
        if (lo >= Samples.Count) return false;
        var after = Samples[lo];
        if (Math.Abs(after.DistanceMeters - distanceMeters) <= 1e-6)
        { elapsedSeconds = after.ElapsedSeconds; return true; }
        if (lo == 0) return false;
        var before = Samples[lo - 1];
        var gap = after.ElapsedSeconds - before.ElapsedSeconds;
        var distanceSpan = after.DistanceMeters - before.DistanceMeters;
        if (!double.IsFinite(gap) || gap <= 0 || gap > maximumGapSeconds || distanceSpan <= 1e-9)
            return false;
        var fraction = Math.Clamp((distanceMeters - before.DistanceMeters) / distanceSpan, 0, 1);
        elapsedSeconds = before.ElapsedSeconds + gap * fraction;
        return true;
    }

    /// <summary>Returns a zero-based trace clipped to one interpolated effort interval.</summary>
    public GhostRideTrace SliceByDistance(double startDistanceMeters, double endDistanceMeters,
        double maximumGapSeconds = 10)
    {
        if (!double.IsFinite(startDistanceMeters) || !double.IsFinite(endDistanceMeters) || endDistanceMeters <= startDistanceMeters ||
            !TryGetElapsedAtDistance(startDistanceMeters, out var startElapsed, maximumGapSeconds) ||
            !TryGetElapsedAtDistance(endDistanceMeters, out var endElapsed, maximumGapSeconds) || endElapsed <= startElapsed)
            throw new ArgumentOutOfRangeException(nameof(startDistanceMeters), "The requested trace interval is unavailable or does not increase.");

        var samples = new List<GhostRideSample> { SampleAt(startElapsed, maximumGapSeconds) with
            { ElapsedSeconds = 0, DistanceMeters = 0 } };
        foreach (var sample in Samples.Where(item => item.ElapsedSeconds > startElapsed && item.ElapsedSeconds < endElapsed))
            samples.Add(sample with { ElapsedSeconds = sample.ElapsedSeconds - startElapsed,
                DistanceMeters = Math.Clamp(sample.DistanceMeters - startDistanceMeters, 0, endDistanceMeters - startDistanceMeters) });
        var end = SampleAt(endElapsed, maximumGapSeconds) with
            { ElapsedSeconds = endElapsed - startElapsed, DistanceMeters = endDistanceMeters - startDistanceMeters };
        if (end.ElapsedSeconds > samples[^1].ElapsedSeconds) samples.Add(end);
        return new GhostRideTrace(samples);
    }
}
