namespace IndoorCycling.Trainer.Abstractions;

public enum WorkoutTrainerControlKind { Unavailable, TargetsOnly, Erg, SimulatedErg }
public sealed record WorkoutTrainerControlStatus(WorkoutTrainerControlKind Kind, string Message,
    int MinimumWatts = 0, int MaximumWatts = 1000, double? MinimumIncrementWatts = null)
{
    public bool CanControlPower => Kind is WorkoutTrainerControlKind.Erg or WorkoutTrainerControlKind.SimulatedErg;
}

/// <summary>Optional downstream workout-control capability. RideSessionNode remains the sole command authority.</summary>
public interface IWorkoutTrainerControl
{
    WorkoutTrainerControlStatus WorkoutControlStatus { get; }
    Task BeginWorkoutControlAsync(long sessionId, CancellationToken cancellationToken = default);
    Task SetWorkoutTargetPowerAsync(int watts, long sessionId, CancellationToken cancellationToken = default);
    Task ReleaseWorkoutControlAsync(long sessionId, CancellationToken cancellationToken = default);
}
