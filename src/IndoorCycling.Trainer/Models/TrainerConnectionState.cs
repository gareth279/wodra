namespace IndoorCycling.Trainer.Models;

public enum TrainerConnectionPhase
{
    Idle,
    Scanning,
    Connecting,
    SettingUp,
    Ready,
    NoTrainer,
    Failed,
    Disconnected,
    Demo
}

/// <summary>Tracks a single UI connection attempt and rejects late results from older attempts.</summary>
public sealed class TrainerConnectionState
{
    public long AttemptId { get; private set; }
    public TrainerConnectionPhase Phase { get; private set; } = TrainerConnectionPhase.Idle;
    public string? Error { get; private set; }
    public bool CanContinue => Phase is TrainerConnectionPhase.Ready or TrainerConnectionPhase.Demo;

    public long BeginAttempt()
    {
        AttemptId++;
        Phase = TrainerConnectionPhase.Scanning;
        Error = null;
        return AttemptId;
    }

    public bool SetPhase(long attemptId, TrainerConnectionPhase phase, string? error = null)
    {
        if (attemptId != AttemptId) return false;
        Phase = phase;
        Error = error;
        return true;
    }

    public void Invalidate()
    {
        AttemptId++;
        Phase = TrainerConnectionPhase.Idle;
        Error = null;
    }
}
