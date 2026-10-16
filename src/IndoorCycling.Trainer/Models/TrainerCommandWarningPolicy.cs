namespace IndoorCycling.Trainer.Models;

public enum TrainerCommandWarningTransition { None, Show, Clear }

/// <summary>
/// Tracks consecutive gradient-command acknowledgement failures for one ride session.
/// A transport acknowledgement clears the warning; telemetry is not evidence of command success.
/// </summary>
public sealed class TrainerCommandWarningPolicy
{
    /// <summary>Show a persistent warning after three consecutive command failures.</summary>
    public const int ConsecutiveFailureWarningThreshold = 3;

    private readonly object _gate = new();
    private long _sessionId;
    private int _consecutiveFailures;
    private bool _warningVisible;

    public long BeginSession()
    {
        lock (_gate)
        {
            _sessionId++;
            _consecutiveFailures = 0;
            _warningVisible = false;
            return _sessionId;
        }
    }

    public void EndSession(long sessionId)
    {
        lock (_gate)
        {
            if (sessionId != _sessionId) return;
            _sessionId++;
            _consecutiveFailures = 0;
            _warningVisible = false;
        }
    }

    public TrainerCommandWarningTransition ObserveFailure(long sessionId)
    {
        lock (_gate)
        {
            if (sessionId != _sessionId) return TrainerCommandWarningTransition.None;
            _consecutiveFailures++;
            if (_consecutiveFailures < ConsecutiveFailureWarningThreshold || _warningVisible)
                return TrainerCommandWarningTransition.None;
            _warningVisible = true;
            return TrainerCommandWarningTransition.Show;
        }
    }

    public TrainerCommandWarningTransition ObserveTransportAcknowledgement(long sessionId)
    {
        lock (_gate)
        {
            if (sessionId != _sessionId) return TrainerCommandWarningTransition.None;
            _consecutiveFailures = 0;
            if (!_warningVisible) return TrainerCommandWarningTransition.None;
            _warningVisible = false;
            return TrainerCommandWarningTransition.Clear;
        }
    }

    public int GetConsecutiveFailureCount(long sessionId)
    {
        lock (_gate) return sessionId == _sessionId ? _consecutiveFailures : 0;
    }

    public bool IsWarningVisible(long sessionId)
    {
        lock (_gate) return sessionId == _sessionId && _warningVisible;
    }

    public bool IsCurrentSession(long sessionId)
    {
        lock (_gate) return sessionId == _sessionId;
    }
}
