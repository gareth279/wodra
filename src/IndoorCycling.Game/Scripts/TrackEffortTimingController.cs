using System;
using System.Collections.Generic;
using System.Linq;

public readonly record struct TrackEffortSplit(int Number, double DistanceMeters, double ElapsedSeconds);
public sealed record TrackEffortResult(string ModeId, double DistanceMeters, double ElapsedSeconds,
    bool Completed, bool Eligible, IReadOnlyList<TrackEffortSplit> Splits, string? InvalidReason);

/// <summary>Frame-rate independent track effort timing over authoritative ride chainage.</summary>
public sealed class TrackEffortTimingController
{
    public const double LapMeters = 250;
    public const double FlyingRunUpMeters = 50;
    private readonly RideActivityMode _mode;
    private readonly double _startDistance;
    private readonly double _startElapsed;
    private readonly List<TrackEffortSplit> _splits = [];
    private double _previousDistance;
    private double _previousElapsed;
    private double _timedStartDistance;
    private double _timedStartElapsed;
    private double _nextSplitDistance;
    private double _finishDistance;
    private bool _timingStarted;
    private string? _invalidReason;

    public TrackEffortResult? Result { get; private set; }
    public bool IsComplete => Result?.Completed == true;
    public string StatusText { get; private set; } = "Ready";
    public string DisplayStatusText => _invalidReason is null ? StatusText : $"Practice · {StatusText}";

    public TrackEffortTimingController(RideActivityMode mode, double initialDistanceMeters = 0,
        double initialElapsedSeconds = 0)
    {
        if (mode is not (RideActivityMode.FreeRide or RideActivityMode.LapTiming or RideActivityMode.Flying200 or
            RideActivityMode.Standing500 or RideActivityMode.Standing1000)) throw new ArgumentOutOfRangeException(nameof(mode));
        _mode = mode;
        _startDistance = _previousDistance = initialDistanceMeters;
        _startElapsed = _previousElapsed = initialElapsedSeconds;
        switch (mode)
        {
            case RideActivityMode.LapTiming:
                _timingStarted = true; _timedStartDistance = initialDistanceMeters; _timedStartElapsed = initialElapsedSeconds;
                _finishDistance = initialDistanceMeters + LapMeters; _nextSplitDistance = _finishDistance;
                StatusText = "Ride one lap · 250 m";
                break;
            case RideActivityMode.Flying200:
                _finishDistance = initialDistanceMeters + FlyingRunUpMeters + 200;
                _nextSplitDistance = initialDistanceMeters + FlyingRunUpMeters;
                StatusText = "Run up to 50 m · flying 200 m starts on the line";
                break;
            case RideActivityMode.Standing500:
                _finishDistance = initialDistanceMeters + 500; _nextSplitDistance = initialDistanceMeters + LapMeters;
                StatusText = "Standing start · ride 500 m";
                break;
            case RideActivityMode.Standing1000:
                _finishDistance = initialDistanceMeters + 1000; _nextSplitDistance = initialDistanceMeters + LapMeters;
                StatusText = "Standing start · ride 1,000 m";
                break;
            default: StatusText = "Free ride · lap count active"; break;
        }
    }

    public void Invalidate(string reason)
    {
        if (Result is null)
        {
            _invalidReason ??= reason;
        }
    }

    public void MarkPractice(string reason) => Invalidate(reason);

    public TrackEffortResult? Advance(double distanceMeters, double elapsedSeconds, bool paused = false,
        bool invalidMovement = false)
    {
        if (!double.IsFinite(distanceMeters) || !double.IsFinite(elapsedSeconds) || distanceMeters < _previousDistance)
        { Invalidate("ride position changed"); return Result; }
        if (paused) Invalidate("pause invalidated the timed attempt");
        if (invalidMovement) Invalidate("repositioning invalidated the timed attempt");
        if (paused)
        {
            _previousDistance = distanceMeters; _previousElapsed = elapsedSeconds;
            return Result;
        }
        if (_mode == RideActivityMode.FreeRide || Result is not null)
        { _previousDistance = distanceMeters; _previousElapsed = elapsedSeconds; return Result; }

        if (_mode == RideActivityMode.Flying200 && !_timingStarted && Crossed(_previousDistance, distanceMeters, _nextSplitDistance))
        {
            _timedStartDistance = _nextSplitDistance;
            _timedStartElapsed = Interpolate(_previousDistance, distanceMeters, _previousElapsed, elapsedSeconds, _nextSplitDistance);
            _timingStarted = true;
            _nextSplitDistance = _timedStartDistance + 100;
            StatusText = "Flying 200 m · timing";
        }
        if ((_mode is RideActivityMode.Standing500 or RideActivityMode.Standing1000) && !_timingStarted && distanceMeters > _startDistance)
        {
            _timingStarted = true;
            _timedStartDistance = _startDistance;
            _timedStartElapsed = _startElapsed;
            StatusText = "Timed effort · underway";
        }
        while (_timingStarted && (_mode is RideActivityMode.Standing500 or RideActivityMode.Standing1000) &&
            _nextSplitDistance < _finishDistance && Crossed(_previousDistance, distanceMeters, _nextSplitDistance))
        {
            AddSplit(_nextSplitDistance, Interpolate(_previousDistance, distanceMeters, _previousElapsed, elapsedSeconds, _nextSplitDistance));
            _nextSplitDistance += LapMeters;
        }
        if (_timingStarted && _mode == RideActivityMode.Flying200 && _nextSplitDistance < _finishDistance &&
            Crossed(_previousDistance, distanceMeters, _nextSplitDistance))
        {
            AddSplit(_nextSplitDistance, Interpolate(_previousDistance, distanceMeters, _previousElapsed, elapsedSeconds, _nextSplitDistance));
            _nextSplitDistance = _finishDistance;
        }
        if (_timingStarted && Crossed(_previousDistance, distanceMeters, _finishDistance))
        {
            var finish = Interpolate(_previousDistance, distanceMeters, _previousElapsed, elapsedSeconds, _finishDistance);
            Result = new TrackEffortResult(_mode.ToString(), _finishDistance - _timedStartDistance,
                finish - _timedStartElapsed, true, _invalidReason is null, _splits.ToArray(), _invalidReason);
            var splitSummary = string.Join(" · ", _splits.Select(split =>
                $"{split.DistanceMeters:0} m {split.ElapsedSeconds:0.00} s"));
            splitSummary = string.IsNullOrEmpty(splitSummary) ? "Finish" : $"{splitSummary} · Finish";
            StatusText = $"Complete · {Result.DistanceMeters:0} m · {Result.ElapsedSeconds:0.00} s\n{splitSummary} {Result.ElapsedSeconds:0.00} s";
        }
        _previousDistance = distanceMeters; _previousElapsed = elapsedSeconds;
        return Result;
    }

    private void AddSplit(double distance, double elapsed)
    {
        var split = new TrackEffortSplit(_splits.Count + 1, distance - _timedStartDistance, elapsed - _timedStartElapsed);
        _splits.Add(split);
        StatusText = $"Split {split.Number} · {split.DistanceMeters:0} m · {split.ElapsedSeconds:0.00} s";
    }
    private static bool Crossed(double before, double after, double line) => before < line && after >= line;
    private static double Interpolate(double beforeD, double afterD, double beforeT, double afterT, double line) =>
        afterD <= beforeD ? afterT : beforeT + (afterT - beforeT) * Math.Clamp((line - beforeD) / (afterD - beforeD), 0, 1);
}
