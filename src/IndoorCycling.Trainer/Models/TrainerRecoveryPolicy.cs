namespace IndoorCycling.Trainer.Models;

/// <summary>Bounded automatic recovery rules used for an unexpectedly lost trainer link.</summary>
public static class TrainerRecoveryPolicy
{
    public const int MaximumAutomaticAttempts = 3;

    public static bool ShouldRetry(int completedAttempts, bool intentionalDisconnect)
        => !intentionalDisconnect && completedAttempts < MaximumAutomaticAttempts;

    public static TimeSpan DelayBeforeAttempt(int attemptNumber)
        => TimeSpan.FromMilliseconds(600 * Math.Clamp(attemptNumber, 1, MaximumAutomaticAttempts));
}
