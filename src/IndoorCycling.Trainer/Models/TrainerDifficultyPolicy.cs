namespace IndoorCycling.Trainer.Models;

public static class TrainerDifficultyPolicy
{
    public const int DefaultPercent = 50;

    public static int Normalize(int percent)
    {
        var bounded = Math.Clamp(percent, 0, 100);
        return Math.Clamp((int)(Math.Round(bounded / 5d, MidpointRounding.AwayFromZero) * 5), 0, 100);
    }

    public static double ScaleGrade(double routeGradientPercent, int difficultyPercent)
    {
        if (!double.IsFinite(routeGradientPercent)) return 0;
        return routeGradientPercent * Normalize(difficultyPercent) / 100d;
    }
}

/// <summary>Short linear transition used only for explicit trainer resistance changes.</summary>
public sealed class TrainerGradeTransition
{
    private double _start;
    private double _target;
    private double _elapsed;
    private double _duration;

    public bool IsActive { get; private set; }
    public double CurrentPercent { get; private set; }

    public void Start(double currentPercent, double targetPercent, double durationSeconds = 3)
    {
        CurrentPercent = FiniteOrZero(currentPercent);
        _start = CurrentPercent;
        _target = FiniteOrZero(targetPercent);
        _elapsed = 0;
        _duration = Math.Max(0.01, double.IsFinite(durationSeconds) ? durationSeconds : 3);
        IsActive = Math.Abs(_target - _start) > 0.01;
        if (!IsActive) CurrentPercent = _target;
    }

    public double Advance(double deltaSeconds, double latestTargetPercent)
    {
        var latestTarget = FiniteOrZero(latestTargetPercent);
        if (!IsActive) return CurrentPercent = latestTarget;
        if (Math.Abs(latestTarget - _target) >= 0.5)
            Start(CurrentPercent, latestTarget, _duration);
        _elapsed += Math.Max(0, double.IsFinite(deltaSeconds) ? deltaSeconds : 0);
        var amount = Math.Clamp(_elapsed / _duration, 0, 1);
        CurrentPercent = _start + (_target - _start) * amount;
        if (amount >= 1) IsActive = false;
        return CurrentPercent;
    }

    public void Cancel(double? currentPercent = null)
    {
        if (currentPercent is { } current) CurrentPercent = FiniteOrZero(current);
        _start = _target = CurrentPercent;
        _elapsed = 0;
        IsActive = false;
    }

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;
}
