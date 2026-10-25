using IndoorCycling.Core.Models;
using IndoorCycling.Trainer.Abstractions;
using IndoorCycling.Trainer.Models;

namespace IndoorCycling.Trainer.Implementations;

public sealed class FakeTrainerClient : ITrainerClient, IWorkoutTrainerControl
{
    private readonly Random _random = new();
    private bool _isConnected;
    private double _gradePercent;
    public double? LastRequestedSimulationGradePercent { get; private set; }
    public double? LastAppliedSimulationGradePercent { get; private set; }
    private int _power = 180;
    private int _cadence = 85;
    private double _speedKph = 30.0;
    private int? _controlledPower;
    private int? _controlledCadence;
    private int? _workoutTargetPowerWatts;
    public bool HasLiveDummyControls => _controlledPower.HasValue;
    public int TargetPowerWatts => _controlledPower ?? _power;
    public int? WorkoutTargetPowerWatts => _workoutTargetPowerWatts;
    public int TargetCadenceRpm => _controlledCadence ?? _cadence;
    private long _workoutSessionId;
    public WorkoutTrainerControlStatus WorkoutControlStatus => new(WorkoutTrainerControlKind.SimulatedErg,
        "Dummy trainer · target response simulated; no physical resistance is controlled.", 0, 1000, 1);
    public Task BeginWorkoutControlAsync(long sessionId, CancellationToken cancellationToken = default)
    { if (sessionId <= 0) throw new ArgumentOutOfRangeException(nameof(sessionId)); Interlocked.Exchange(ref _workoutSessionId, sessionId); return Task.CompletedTask; }
    public Task SetWorkoutTargetPowerAsync(int watts, long sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId != Interlocked.Read(ref _workoutSessionId)) throw new OperationCanceledException("Stale dummy workout target.");
        _workoutTargetPowerWatts = Math.Clamp(watts, 0, 1000); return Task.CompletedTask;
    }
    public Task ReleaseWorkoutControlAsync(long sessionId, CancellationToken cancellationToken = default)
    { if (sessionId == Interlocked.Read(ref _workoutSessionId)) { _workoutTargetPowerWatts = null; Interlocked.CompareExchange(ref _workoutSessionId, 0, sessionId); } return Task.CompletedTask; }

    public void SetDummyOutput(int powerWatts, int cadenceRpm)
    {
        _controlledPower = Math.Clamp(powerWatts, 0, 1500);
        _controlledCadence = Math.Clamp(cadenceRpm, 0, 160);
        _power = _controlledPower.Value;
        _cadence = _controlledCadence.Value;
    }

    public bool IsConnected => _isConnected;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _isConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _isConnected = false;
        Interlocked.Exchange(ref _workoutSessionId, 0);
        return Task.CompletedTask;
    }

    public Task SetSimulationAsync(
        SimulationCommand command,
        CancellationToken cancellationToken = default)
    {
        _gradePercent = command.GradePercent;
        LastRequestedSimulationGradePercent = command.GradePercent;
        LastAppliedSimulationGradePercent = command.GradePercent;
        return Task.CompletedTask;
    }

    public Task SetResistanceAsync(
        ResistanceCommand command,
        CancellationToken cancellationToken = default)
    {
        // Placeholder for later
        return Task.CompletedTask;
    }

    public TrainerTelemetry GetLatestTelemetry()
    {
        if (!_isConnected)
        {
            return new TrainerTelemetry(
                Timestamp: DateTimeOffset.UtcNow,
                PowerWatts: 0,
                CadenceRpm: 0,
                SpeedKph: 0
            );
        }

        // crude fake behaviour
        if (_controlledPower is { } targetPower && _controlledCadence is { } targetCadence)
        {
            _power = targetPower;
            _cadence = targetCadence;
            _speedKph = Math.Max(8.0, 32.0 + (_power - 180) * 0.03 - (_gradePercent * 1.8));
            return new TrainerTelemetry(DateTimeOffset.UtcNow, _power, _cadence, _speedKph);
        }
        _power = Clamp(_power + _random.Next(-8, 9) - (int)Math.Round(_gradePercent), 120, 320);
        _cadence = Clamp(_cadence + _random.Next(-2, 3), 70, 100);
        _speedKph = Math.Max(8.0, 32.0 + (_power - 180) * 0.03 - (_gradePercent * 1.8));

        return new TrainerTelemetry(
            Timestamp: DateTimeOffset.UtcNow,
            PowerWatts: _power,
            CadenceRpm: _cadence,
            SpeedKph: _speedKph
        );
    }

    private static int Clamp(int value, int min, int max) =>
        Math.Min(max, Math.Max(min, value));
}
