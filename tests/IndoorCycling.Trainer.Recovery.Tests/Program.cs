using IndoorCycling.Core.Models;
using IndoorCycling.Trainer.Models;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS {message}");
}

var attemptCount = 0;
var delayedReturn = await TrainerRecoveryLoop.RunAsync(
    (attempt, _) => Task.FromResult(++attemptCount >= 3),
    maximumAttempts: 8,
    recoveryWindow: TimeSpan.FromSeconds(5),
    _ => TimeSpan.Zero,
    CancellationToken.None);
Check(delayedReturn.Outcome == TrainerRecoveryOutcome.Recovered && delayedReturn.Attempts == 3,
    "trainer unavailable initially and returning after a delay is rediscovered");

attemptCount = 0;
var retryAfterGattFailures = await TrainerRecoveryLoop.RunAsync(
    (attempt, _) =>
    {
        attemptCount++;
        if (attempt == 1) throw new InvalidOperationException("ftms_gatt_service_discovery: Unreachable");
        return Task.FromResult(attempt >= 3);
    },
    maximumAttempts: 8,
    recoveryWindow: TimeSpan.FromSeconds(5),
    _ => TimeSpan.Zero,
    CancellationToken.None);
Check(retryAfterGattFailures.Outcome == TrainerRecoveryOutcome.Recovered && retryAfterGattFailures.Attempts == 3,
    "failed GATT attempts can be followed by a successful recovery");

var expired = await TrainerRecoveryLoop.RunAsync(
    (_, _) => Task.FromResult(false),
    maximumAttempts: 2,
    recoveryWindow: TimeSpan.FromSeconds(5),
    _ => TimeSpan.Zero,
    CancellationToken.None);
var manualRetry = await TrainerRecoveryLoop.RunAsync(
    (_, _) => Task.FromResult(true),
    maximumAttempts: 8,
    recoveryWindow: TimeSpan.FromSeconds(5),
    _ => TimeSpan.Zero,
    CancellationToken.None);
Check(expired.Outcome == TrainerRecoveryOutcome.Expired && manualRetry.Outcome == TrainerRecoveryOutcome.Recovered,
    "recovery expiry offers a manual retry that can recover");

var timeExpired = await TrainerRecoveryLoop.RunAsync(
    async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return false; },
    maximumAttempts: 8,
    recoveryWindow: TimeSpan.FromMilliseconds(25),
    _ => TimeSpan.Zero,
    CancellationToken.None);
Check(timeExpired.Outcome == TrainerRecoveryOutcome.Expired,
    "automatic recovery is bounded by its time window even during an in-flight attempt");

var intentionalAttempts = 0;
var intentional = await TrainerRecoveryLoop.RunAsync(
    (_, _) => { intentionalAttempts++; return Task.FromResult(false); },
    maximumAttempts: 8,
    recoveryWindow: TimeSpan.FromSeconds(5),
    _ => TimeSpan.Zero,
    CancellationToken.None,
    shouldContinue: () => false);
Check(intentional.Outcome == TrainerRecoveryOutcome.Cancelled && intentionalAttempts == 0,
    "intentional disconnect suppresses recovery");

var connectionState = new TrainerConnectionState();
var staleAttemptId = connectionState.BeginAttempt();
var currentAttemptId = connectionState.BeginAttempt();
Check(!connectionState.SetPhase(staleAttemptId, TrainerConnectionPhase.Ready) &&
      connectionState.SetPhase(currentAttemptId, TrainerConnectionPhase.Connecting),
    "late connection callbacks cannot replace the current attempt state");

var flux = new BleDiscoveredDevice(22, "Tacx Flux 2", [], -50);
var returningFlux = new BleDiscoveredDevice(33, "Tacx Flux 2", [], -48);
var otherTrainer = new BleDiscoveredDevice(44, "Tacx Neo", [], -35);
Check(TrainerRecoveryPolicy.SelectReturningTrainer([returningFlux, otherTrainer], 22, "Tacx Flux 2") == returningFlux,
    "rediscovery follows selected trainer identity when its BLE address changes");
Check(TrainerRecoveryPolicy.SelectReturningTrainer([otherTrainer], 22, "Tacx Flux 2") is null,
    "rediscovery does not switch to a different trainer when the selected one is absent");
Check(TrainerRecoveryPolicy.SelectReturningTrainer([flux], 22, "Tacx Flux 2") == flux,
    "rediscovery prefers the selected trainer address when it remains stable");

var now = DateTimeOffset.UtcNow;
var stationaryZero = new TrainerTelemetry(now, 0, 0, 0, HasPowerMeasurement: true,
    HasCadenceMeasurement: true, HasReceivedMeasurement: true, HasSpeedMeasurement: true);
var missing = new TrainerTelemetry(DateTimeOffset.MinValue, 0, 0, 0, HasPowerMeasurement: false,
    HasCadenceMeasurement: false, HasReceivedMeasurement: false, HasSpeedMeasurement: false);
Check(TrainerTelemetryPresentation.Power(stationaryZero, now).State == TelemetryReadingState.Fresh &&
      TrainerTelemetryPresentation.Power(stationaryZero, now).Value == 0 &&
      TrainerTelemetryPresentation.Power(missing, now).State == TelemetryReadingState.Awaiting,
    "stationary zero telemetry remains distinct from missing telemetry");

Check(TrainerRecoveryPolicy.MaximumAutomaticAttempts == 8 &&
      TrainerRecoveryPolicy.AutomaticRecoveryWindow >= TimeSpan.FromSeconds(26) &&
      TrainerRecoveryPolicy.DelayBeforeAttempt(1) == TimeSpan.Zero &&
      TrainerRecoveryPolicy.DelayBeforeAttempt(8) >= TimeSpan.FromSeconds(7),
    "retry schedule provides time for trainer reboot and advertising restart");
