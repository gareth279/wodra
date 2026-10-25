namespace IndoorCycling.Core.Workouts;

public sealed record WorkoutIntervalSnapshot(int Index, int Count, WorkoutStep Step,
    double ElapsedInStepSeconds, double RemainingInStepSeconds, int TargetWatts, int? NextTargetWatts,
    string? NextName, double ElapsedSeconds, double TotalSeconds, double Progress, double IntensityFactor,
    bool IsComplete);

/// <summary>Deterministic interval transport; it owns targets and time only, never telemetry or trainer IO.</summary>
public sealed class WorkoutSessionEngine
{
    private readonly WorkoutDefinition _definition;
    private readonly int? _ftp;
    private int _index;
    private double _elapsedInStep;
    private double _elapsed;
    private double _intensityFactor = 1;
    public event Action<int>? IntervalChanged;
    public WorkoutDefinition Definition => _definition;
    public int? FtpWatts => _ftp;
    public bool IsComplete => _index >= _definition.Steps.Count;
    public double ElapsedSeconds => _elapsed;

    public WorkoutSessionEngine(WorkoutDefinition definition, int? ftpWatts)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _ftp = ftpWatts;
        _definition.Validate(ftpWatts);
    }

    public WorkoutIntervalSnapshot Snapshot()
    {
        if (IsComplete)
        {
            var last = _definition.Steps[^1];
            return new(_index, _definition.Steps.Count, last, last.DurationSeconds, 0, 0, null, null,
                _elapsed, _definition.DurationSeconds, 1, _intensityFactor, true);
        }
        var step = _definition.Steps[_index];
        var progress = Math.Clamp(_elapsedInStep / step.DurationSeconds, 0, 1);
        var from = step.StartTarget.ResolveWatts(_ftp);
        var to = (step.EndTarget ?? step.StartTarget).ResolveWatts(_ftp);
        var target = Scale((int)Math.Round((double)from + (to - from) * progress));
        var next = _index + 1 < _definition.Steps.Count ? _definition.Steps[_index + 1] : null;
        return new(_index, _definition.Steps.Count, step, _elapsedInStep,
            Math.Max(0, step.DurationSeconds - _elapsedInStep), target,
            next is null ? null : Scale(next.StartTarget.ResolveWatts(_ftp)), next?.Name,
            _elapsed, _definition.DurationSeconds, Math.Clamp(_elapsed / _definition.DurationSeconds, 0, 1), _intensityFactor, false);
    }

    /// <summary>Advances across boundaries exactly once, including large frame deltas.</summary>
    public int Advance(double deltaSeconds, bool paused = false)
    {
        if (paused || IsComplete || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0) return 0;
        var remaining = deltaSeconds;
        var transitions = 0;
        while (remaining > 0 && !IsComplete)
        {
            var step = _definition.Steps[_index];
            var available = step.DurationSeconds - _elapsedInStep;
            var consumed = Math.Min(remaining, available);
            _elapsedInStep += consumed; _elapsed += consumed; remaining -= consumed;
            if (_elapsedInStep + 1e-9 < step.DurationSeconds) break;
            _index++; _elapsedInStep = 0; transitions++;
            IntervalChanged?.Invoke(_index);
        }
        return transitions;
    }

    public bool SkipInterval()
    {
        if (IsComplete) return false;
        _index++; _elapsedInStep = 0; IntervalChanged?.Invoke(_index); return true;
    }

    public double AdjustIntensity(double delta)
    {
        if (!double.IsFinite(delta)) return _intensityFactor;
        _intensityFactor = Math.Clamp(_intensityFactor + delta, .7, 1.3);
        return _intensityFactor;
    }

    private int Scale(int watts) => Math.Clamp((int)Math.Round(watts * _intensityFactor), 0, 1000);
}
