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
}
