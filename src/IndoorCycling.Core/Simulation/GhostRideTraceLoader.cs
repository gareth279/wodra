using Dynastream.Fit;
using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Simulation;

/// <summary>Uses the FIT SDK already used by FitRideLoader; never retains GPS or unrelated messages.</summary>
public sealed class GhostRideTraceLoader
{
    public const double MaximumInterpolationGapSeconds = 10;

    public GhostRideTrace Load(string path)
    {
        var frames = new FitRideLoader().Load(path);
        if (frames.Count < 2) throw new InvalidOperationException("FIT has fewer than two timestamped ride records.");
        var timerEvents = new FitRideLoader().LoadTimerEvents(path);
        var samples = new List<GhostRideSample>(frames.Count);
        var eventIndex = 0;
        var running = true;
        while (eventIndex < timerEvents.Count && timerEvents[eventIndex].Timestamp <= frames[0].Timestamp)
            running = timerEvents[eventIndex++].Running;
        var priorTimestamp = frames[0].Timestamp;
        var activeElapsed = 0d;
        var previousDistance = -1d;
        foreach (var f in frames)
        {
            if (samples.Count > 0)
            {
                while (eventIndex < timerEvents.Count && timerEvents[eventIndex].Timestamp <= f.Timestamp)
                {
                    var evt = timerEvents[eventIndex++];
                    if (evt.Timestamp > priorTimestamp && running)
                        activeElapsed += (evt.Timestamp - priorTimestamp).TotalSeconds;
                    priorTimestamp = evt.Timestamp;
                    running = evt.Running;
                }
                if (f.Timestamp > priorTimestamp && running)
                    activeElapsed += (f.Timestamp - priorTimestamp).TotalSeconds;
                priorTimestamp = f.Timestamp;
            }
            var elapsed = activeElapsed;
            if (elapsed < 0 || f.DistanceMeters is not { } d || !double.IsFinite(d)) continue;
            if (previousDistance >= 0 && d < previousDistance - 2) throw new InvalidOperationException("FIT distance decreases beyond recorder jitter.");
            if (samples.Count > 0 && elapsed <= samples[^1].ElapsedSeconds) continue;
            samples.Add(new GhostRideSample(elapsed, Math.Max(previousDistance, d), f.SpeedMps, f.PowerWatts, f.CadenceRpm));
            previousDistance = Math.Max(previousDistance, d);
        }
        for (var i = 1; i < samples.Count; i++)
            if (samples[i].ElapsedSeconds - samples[i - 1].ElapsedSeconds > MaximumInterpolationGapSeconds)
                throw new InvalidOperationException("FIT contains a record gap too large for safe ghost playback.");
        return new GhostRideTrace(samples);
    }
}
