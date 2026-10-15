using IndoorCycling.Core.Models;
using IndoorCycling.Trainer.Models;
using System.Text;
using System.Text.Json;

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

var fecPower = new AntFeCPowerAccumulator();
var rawAntKey = new byte[] { 1, 35, 69, 103, 137, 171, 205, 239 };
Check(AntNetworkKeyMaterial.Parse(rawAntKey).SequenceEqual(rawAntKey),
    "private ANT+ build configuration accepts an eight-byte binary key");
var textAntKey = Encoding.UTF8.GetBytes("0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef");
Check(AntNetworkKeyMaterial.Parse(textAntKey).SequenceEqual(rawAntKey),
    "private ANT+ build configuration parses formatted hexadecimal key material");
Check(AntNetworkKeyMaterial.Parse(Encoding.UTF8.GetBytes("0123456789abcdef")).SequenceEqual(rawAntKey),
    "private ANT+ build configuration parses compact hexadecimal key material");
try
{
    _ = AntNetworkKeyMaterial.LoadEmbedded(typeof(AntNetworkKeyMaterial).Assembly);
    throw new InvalidOperationException("A build without ANT+ runtime configuration was accepted.");
}
catch (InvalidOperationException error) when (error.Message.Contains("ANT+ FE-C runtime configuration is missing", StringComparison.Ordinal))
{
    Check(true, "ANT+ runtime initialization reports a missing private build configuration clearly");
}
try
{
    _ = AntNetworkKeyMaterial.Parse(Encoding.UTF8.GetBytes("not-a-network-key"));
    throw new InvalidOperationException("Invalid ANT+ build configuration was accepted.");
}
catch (InvalidDataException)
{
    Check(true, "invalid ANT+ build configuration is rejected without including its value in the error");
}
Check(fecPower.Observe(254, 65530, 200) == 200, "FE-C uses instantaneous power for the first sample");
Check(fecPower.Observe(1, 4, 210) == 3, "FE-C event and accumulated-power counters handle rollover");
Check(fecPower.Observe(1, 4, 0x0FFF) is null, "FE-C invalid instantaneous power stays unavailable when no new event arrives");
var invalidRecoveryPower = new AntFeCPowerAccumulator();
invalidRecoveryPower.Observe(20, 500, 220);
Check(invalidRecoveryPower.Observe(21, 900, 0x0FFF) is null &&
      invalidRecoveryPower.Observe(22, 920, 160) == 160,
    "FE-C invalid power resets accumulated counters instead of fabricating a recovery value");
var fecTrainerPage = new byte[] { 25, 3, 88, 0x10, 0x00, 150, 0x00, 0x00 };
var fecTrainer = AntFeCProtocol.DecodeTelemetry(fecTrainerPage, DateTimeOffset.UtcNow, new AntFeCPowerAccumulator());
Check(fecTrainer.CadenceRpm == 88 && fecTrainer.HasCadenceMeasurement && fecTrainer.HasReceivedMeasurement,
    "FE-C trainer page decodes cadence and reports a real measurement");
var fecGeneralPage = new byte[] { 16, 25, 0, 0, 0x40, 0x1F, 0xFF, 0x00 };
var fecGeneral = AntFeCProtocol.DecodeTelemetry(fecGeneralPage, DateTimeOffset.UtcNow, new AntFeCPowerAccumulator());
Check(Math.Abs(fecGeneral.SpeedKph - 28.8) < 0.001 && !fecGeneral.HasPowerMeasurement,
    "FE-C general page converts speed without inventing power");
var fecSimulation = AntFeCProtocol.BuildTrackResistance(12.34);
Check(fecSimulation[0] == 51 && fecSimulation[5] == 0xF2 && fecSimulation[6] == 0x52,
    "FE-C simulation command encodes grade using the profile offset");
var fecRequest = AntFeCProtocol.BuildPageRequest(AntFeCProtocol.PageCapabilities);
Check(fecRequest.Length == 8 && fecRequest[0] == 70 && fecRequest[6] == 54,
    "FE-C on-demand request targets the capabilities page");
Check(AntFeCProtocol.ShouldRetryCapabilityRequest(false, 0) &&
      AntFeCProtocol.ShouldRetryCapabilityRequest(false, AntFeCProtocol.MaximumCapabilityRequestAttempts - 1) &&
      !AntFeCProtocol.ShouldRetryCapabilityRequest(false, AntFeCProtocol.MaximumCapabilityRequestAttempts) &&
      !AntFeCProtocol.ShouldRetryCapabilityRequest(true, 0),
    "FE-C capability requests retry only while the page is missing and attempts remain");
var sdkExtendedScanFrame = new byte[] { 0, 0x34, 0x12, 0x91, 0x01, 16, 0, 0, 0, 0, 0, 0, 0 };
var parsedExtendedScan = AntFeCScanFrameParser.Parse(0x5D, sdkExtendedScanFrame);
Check(parsedExtendedScan.IsExtended && parsedExtendedScan.PayloadLength == 8 &&
      parsedExtendedScan.DataPage == AntFeCProtocol.PageGeneral &&
      parsedExtendedScan.Source == new AntFeCDeviceIdentity(0x1234, AntFeCProtocol.DeviceType, 1) &&
      parsedExtendedScan.RejectionReason is null,
    "ANT SDK extended broadcast frames from channelResponse identify FE-C source and page");
var sdkFlagExtendedBroadcast = new byte[] { 0, 25, 0, 0, 0, 0, 0, 0, 0, 0x80, 0x78, 0x56, 0x91, 0x02 };
var parsedFlagExtended = AntFeCScanFrameParser.Parse(0x4E, sdkFlagExtendedBroadcast);
Check(parsedFlagExtended.IsExtended && parsedFlagExtended.PayloadLength == 8 &&
      parsedFlagExtended.Source == new AntFeCDeviceIdentity(0x5678, AntFeCProtocol.DeviceType, 2),
    "ANT SDK ordinary broadcast frames with extended channel-ID flag are parsed");
var wrongTrainerType = new byte[] { 0, 1, 0, 18, 1, 25, 0, 0, 0, 0, 0, 0, 0 };
Check(AntFeCScanFrameParser.Parse(0x5D, wrongTrainerType).RejectionReason == "device_type_mismatch",
    "ANT SDK source device type is masked and mismatches are diagnosed explicitly");
var receiveCounts = new AntReceiveDiagnosticCounts();
receiveCounts.Add(0x4E, "channelResponse", true, 8, AntFeCProtocol.PageGeneral, "accepted_fe_c_page");
receiveCounts.Add(0x4E, "channelResponse", true, 8, AntFeCProtocol.PageGeneral, "accepted_fe_c_page");
var receiveCount = receiveCounts.Snapshot().Single();
Check(receiveCount.MessageId == 0x4E && receiveCount.CallbackSource == "channelResponse" &&
      receiveCount.Extended && receiveCount.PayloadLength == 8 && receiveCount.DataPage == AntFeCProtocol.PageGeneral &&
      receiveCount.Count == 2,
    "ANT receive diagnostics aggregate bounded message, source, extension, length, and outcome counts");
var boundedCounts = new AntReceiveDiagnosticCounts();
for (var i = 0; i < 70; i++) boundedCounts.Add((byte)i, "channelResponse", false, i, null, "unsupported_message_id");
Check(boundedCounts.Snapshot().Count() == 65 &&
      boundedCounts.Snapshot().Last().RejectionReason == "diagnostic_bucket_limit",
    "ANT receive diagnostics cap unique frame buckets and aggregate excess observations");
Check(!AntFeCProtocol.IsReady(true, true, false) && AntFeCProtocol.IsReady(true, true, true) &&
      !AntFeCProtocol.IsReady(true, false, true),
    "FE-C readiness requires telemetry pages and capabilities returned by its request");
var antDiagnostic = System.Text.Json.JsonSerializer.Serialize(new AntFeCDiagnosticEvent(
    "channel_configuration", "command_completed", 12.5, "CHANNEL_ID_0x51",
    ResponseId: 0x40, EventCode: 0x15, DataPage: AntFeCProtocol.PageCapabilities));
Check(antDiagnostic.Contains("CHANNEL_ID_0x51", StringComparison.Ordinal) &&
      antDiagnostic.Contains("21", StringComparison.Ordinal) &&
      !antDiagnostic.Contains("NetworkKey", StringComparison.OrdinalIgnoreCase),
    "ANT stage diagnostics retain command, response, event, and page identifiers without key fields");
var fecConfig = AntFeCProtocol.BuildUserConfiguration(75, 10);
Check(fecConfig[0] == 55 && fecConfig[1] == 0x4C && fecConfig[2] == 0x1D &&
      fecConfig[4] == 0x8F && fecConfig[5] == 0x0C,
    "FE-C simulation sends rider and bicycle weights in user configuration");
var fecStatus = AntFeCCommandStatus.TryParse(new byte[] { 71, 51, 4, 0, 0, 0, 0, 0 });
Check(fecStatus is { IsSuccessful: true, CommandId: 51, Sequence: 4 },
    "FE-C command status confirms the control page");

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

var newTrainerSettings = new TrainerSettings();
Check(newTrainerSettings.ProtocolPreference == TrainerProtocolPreference.Auto,
    "new settings default to deterministic Auto protocol selection");
var legacyTrainerSettings = new TrainerSettings();
Check(TrainerProtocolPreferencePolicy.MigrateLegacyPreference("{\"Rider\":{}}", legacyTrainerSettings) &&
      legacyTrainerSettings.ProtocolPreference == TrainerProtocolPreference.BluetoothFtms,
    "legacy installations retain their Bluetooth FTMS behavior");
var savedTrainerSettings = new TrainerSettings { ProtocolPreference = TrainerProtocolPreference.AntFeC };
Check(!TrainerProtocolPreferencePolicy.MigrateLegacyPreference(
          "{\"Trainer\":{\"ProtocolPreference\":\"AntFeC\"}}", savedTrainerSettings) &&
      savedTrainerSettings.ProtocolPreference == TrainerProtocolPreference.AntFeC,
    "an explicit saved protocol choice survives settings migration");
var serializedTrainerSettings = JsonSerializer.Serialize(new TrainerSettings
{
    ProtocolPreference = TrainerProtocolPreference.AntFeC,
    LastAntDeviceNumber = 42,
    LastAntDeviceType = 17,
    LastAntTransmissionType = 5
});
var restoredTrainerSettings = JsonSerializer.Deserialize<TrainerSettings>(serializedTrainerSettings)!;
Check(restoredTrainerSettings.ProtocolPreference == TrainerProtocolPreference.AntFeC &&
      restoredTrainerSettings.LastAntDeviceNumber == 42 && restoredTrainerSettings.LastAntDeviceType == 17 &&
      restoredTrainerSettings.LastAntTransmissionType == 5,
    "protocol preference and ANT identity persist independently of the Bluetooth address");
var autoBoth = TrainerProtocolPreferencePolicy.Select(TrainerProtocolPreference.Auto, true, true);
var autoAntOnly = TrainerProtocolPreferencePolicy.Select(TrainerProtocolPreference.Auto, false, true);
var explicitUnavailable = TrainerProtocolPreferencePolicy.Select(TrainerProtocolPreference.AntFeC, true, false);
Check(autoBoth.Protocol == TrainerProtocol.BluetoothFtms && autoAntOnly.Protocol == TrainerProtocol.AntFeC,
    "Auto deterministically prefers Bluetooth and uses ANT only when it is the available provider");
Check(!explicitUnavailable.IsAvailable && explicitUnavailable.UnavailableReason is not null,
    "an unavailable explicit protocol is reported instead of silently switching providers");
var antCandidates = AntFeCDiscoveryPolicy.Select([
    new AntFeCDeviceIdentity(44, 17, 1),
    new AntFeCDeviceIdentity(22, 17, 2),
    new AntFeCDeviceIdentity(44, 17, 1),
    new AntFeCDeviceIdentity(99, 120, 1)
]);
Check(antCandidates.Outcome == AntFeCDiscoveryOutcome.Choose && antCandidates.Candidates.Count == 2 &&
      antCandidates.Candidates[0].DeviceNumber == 22 && antCandidates.Candidates[1].DeviceNumber == 44,
    "ANT discovery deduplicates FE-C identities and exposes a stable multiple-trainer choice");
Check(AntFeCDiscoveryPolicy.Select([new AntFeCDeviceIdentity(12, 17, 1)]).Selected?.DeviceNumber == 12 &&
      AntFeCDiscoveryPolicy.Select([]).Outcome == AntFeCDiscoveryOutcome.None,
    "ANT discovery auto-selects one FE-C trainer and rejects empty scans");
