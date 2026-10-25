namespace IndoorCycling.Core.Workouts;

public enum WorkoutBlockKind { WarmUp, Work, Recovery, CoolDown }
public enum WorkoutTargetUnit { Watts, FtpPercent }

public sealed record WorkoutTarget(double Value, WorkoutTargetUnit Unit)
{
    public int ResolveWatts(int? ftpWatts)
    {
        if (!double.IsFinite(Value) || Value < 0 || Value > (Unit == WorkoutTargetUnit.Watts ? 1500 : 2.5))
            throw new ArgumentOutOfRangeException(nameof(Value));
        if (Unit == WorkoutTargetUnit.FtpPercent && ftpWatts is not (>= 50 and <= 1000))
            throw new InvalidOperationException("Set an explicit FTP from 50 to 1000 W before starting this FTP-based workout.");
        return (int)Math.Round(Unit == WorkoutTargetUnit.Watts ? Value : ftpWatts!.Value * Value,
            MidpointRounding.AwayFromZero);
    }
}

public sealed record WorkoutStep(string Id, string Name, WorkoutBlockKind Kind, double DurationSeconds,
    WorkoutTarget StartTarget, WorkoutTarget? EndTarget = null, int? CadenceCueRpm = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Workout steps need stable IDs and names.");
        if (!double.IsFinite(DurationSeconds) || DurationSeconds is < 1 or > 7200) throw new ArgumentOutOfRangeException(nameof(DurationSeconds));
        _ = StartTarget.ResolveWatts(StartTarget.Unit == WorkoutTargetUnit.FtpPercent ? 200 : null);
        if (EndTarget is { } end) _ = end.ResolveWatts(end.Unit == WorkoutTargetUnit.FtpPercent ? 200 : null);
        if (EndTarget is { } target && target.Unit != StartTarget.Unit) throw new ArgumentException("Ramp endpoints must use the same target unit.");
        if (CadenceCueRpm is < 30 or > 180) throw new ArgumentOutOfRangeException(nameof(CadenceCueRpm));
    }
}

public sealed record WorkoutDefinition(string Id, string Name, string Description,
    IReadOnlyList<WorkoutStep> Steps, string Source = "Built in")
{
    public double DurationSeconds => Steps.Sum(step => step.DurationSeconds);
    public void Validate(int? ftpWatts = null)
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name) || Steps.Count is < 1 or > 256)
            throw new ArgumentException("Workout identity or step count is invalid.");
        if (DurationSeconds > 4 * 60 * 60) throw new ArgumentOutOfRangeException(nameof(Steps), "Expanded workout duration may not exceed four hours.");
        foreach (var step in Steps)
        {
            step.Validate();
            _ = step.StartTarget.ResolveWatts(ftpWatts);
            if (step.EndTarget is { } end) _ = end.ResolveWatts(ftpWatts);
        }
    }
}

public static class WorkoutCatalog
{
    private static WorkoutTarget P(double value) => new(value, WorkoutTargetUnit.FtpPercent);
    private static WorkoutTarget W(double value) => new(value, WorkoutTargetUnit.Watts);
    public static IReadOnlyList<WorkoutDefinition> BuiltIns { get; } =
    [
        new("steady-30", "Steady Endurance", "Thirty minutes of smooth, steady aerobic work.",
        [new("warmup", "Warm-up", WorkoutBlockKind.WarmUp, 300, P(.45), P(.70), 85),
         new("steady", "Steady endurance", WorkoutBlockKind.Work, 1200, P(.72), null, 90),
         new("cooldown", "Cool-down", WorkoutBlockKind.CoolDown, 300, P(.60), P(.40), 80)]),
        new("intervals-3x", "Three-minute intervals", "Three controlled efforts with easy recoveries.",
        [new("warmup", "Warm-up", WorkoutBlockKind.WarmUp, 300, P(.45), P(.75), 85),
         new("work-1", "Interval 1", WorkoutBlockKind.Work, 180, P(1.05), null, 95),
         new("recovery-1", "Recovery", WorkoutBlockKind.Recovery, 180, P(.55)),
         new("work-2", "Interval 2", WorkoutBlockKind.Work, 180, P(1.05), null, 95),
         new("recovery-2", "Recovery", WorkoutBlockKind.Recovery, 180, P(.55)),
         new("work-3", "Interval 3", WorkoutBlockKind.Work, 180, P(1.05), null, 95),
         new("cooldown", "Cool-down", WorkoutBlockKind.CoolDown, 300, P(.60), P(.40), 80)]),
        new("ramp-20", "Progressive ramp", "Build gradually, then recover and cool down.",
        [new("ramp", "Progressive work", WorkoutBlockKind.Work, 600, P(.55), P(1.0), 90),
         new("recovery", "Easy recovery", WorkoutBlockKind.Recovery, 300, P(.50)),
         new("cooldown", "Cool-down", WorkoutBlockKind.CoolDown, 300, P(.50), P(.40), 80)])
    ];
    public static WorkoutDefinition? Find(string id) => BuiltIns.FirstOrDefault(item => item.Id == id);
}
