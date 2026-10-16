namespace IndoorCycling.Trainer.Models;

/// <summary>Bounded rediscovery and reconnection rules for an unexpectedly lost trainer link.</summary>
public static class TrainerRecoveryPolicy
{
    public const int MaximumAutomaticAttempts = 8;
    public static readonly TimeSpan AutomaticRecoveryWindow = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan DiscoveryDuration = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan AntDiscoveryWindow = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan AntTelemetryLossTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Spreads attempts over the normal trainer power-cycle and advertising restart window.</summary>
    public static TimeSpan DelayBeforeAttempt(int attemptNumber)
        => TimeSpan.FromSeconds(Math.Clamp(attemptNumber - 1, 0, 7));

    public static bool ShouldRetry(int completedAttempts, bool intentionalDisconnect)
        => !intentionalDisconnect && completedAttempts < MaximumAutomaticAttempts;

    /// <summary>
    /// Matches the remembered trainer after a BLE address changes. Never silently switch to a differently
    /// named trainer when the rider selected a named device.
    /// </summary>
    public static BleDiscoveredDevice? SelectReturningTrainer(
        IEnumerable<BleDiscoveredDevice> discovered,
        ulong? preferredAddress,
        string? preferredName)
    {
        var compatible = discovered.Where(TrainerDiscoveryPolicy.IsCompatible)
            .GroupBy(device => device.BluetoothAddress)
            .Select(group => group.OrderByDescending(device => device.Rssi).First())
            .ToArray();

        if (preferredAddress is { } address)
        {
            var byAddress = compatible.FirstOrDefault(device => device.BluetoothAddress == address);
            if (byAddress is not null) return byAddress;
        }

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var byName = compatible.Where(device => string.Equals(
                device.Name.Trim(), preferredName.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byName.Length == 1) return byName[0];
            return null;
        }

        return compatible.Length == 1 ? compatible[0] : null;
    }
}

public enum TrainerRecoveryOutcome { Recovered, Expired, Cancelled }

public sealed record TrainerRecoveryResult(
    TrainerRecoveryOutcome Outcome,
    int Attempts,
    string? LastFailure);

/// <summary>Shared bounded retry loop used by the UI and deterministic trainer recovery tests.</summary>
public static class TrainerRecoveryLoop
{
    public static async Task<TrainerRecoveryResult> RunAsync(
        Func<int, CancellationToken, Task<bool>> attemptRecovery,
        int maximumAttempts,
        TimeSpan recoveryWindow,
        Func<int, TimeSpan> delayBeforeAttempt,
        CancellationToken cancellationToken,
        Func<bool>? shouldContinue = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(attemptRecovery);
        ArgumentNullException.ThrowIfNull(delayBeforeAttempt);
        if (maximumAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        if (recoveryWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(recoveryWindow));

        shouldContinue ??= static () => true;
        delayAsync ??= static (delay, token) => Task.Delay(delay, token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(recoveryWindow);
        var failures = 0;
        string? lastFailure = null;

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            if (!shouldContinue()) return new(TrainerRecoveryOutcome.Cancelled, failures, lastFailure);
            try
            {
                var delay = delayBeforeAttempt(attempt);
                if (delay > TimeSpan.Zero) await delayAsync(delay, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                if (!shouldContinue()) return new(TrainerRecoveryOutcome.Cancelled, failures, lastFailure);

                if (await attemptRecovery(attempt, deadline.Token))
                    return new(TrainerRecoveryOutcome.Recovered, attempt, lastFailure);
                failures++;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return new(shouldContinue() && !cancellationToken.IsCancellationRequested
                    ? TrainerRecoveryOutcome.Expired : TrainerRecoveryOutcome.Cancelled, failures, lastFailure);
            }
            catch (Exception ex)
            {
                failures++;
                lastFailure = ex.Message;
            }
        }

        return new(shouldContinue() ? TrainerRecoveryOutcome.Expired : TrainerRecoveryOutcome.Cancelled,
            failures, lastFailure);
    }
}
