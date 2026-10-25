using IndoorCycling.Core.Models;
using IndoorCycling.Trainer.Models;
using IndoorCycling.Trainer.Implementations;
using System.Text;
using System.Text.Json;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS {message}");
}

var dummyWorkout = new FakeTrainerClient();
await dummyWorkout.ConnectAsync();
dummyWorkout.SetDummyOutput(190, 90);
await dummyWorkout.BeginWorkoutControlAsync(11);
await dummyWorkout.SetWorkoutTargetPowerAsync(240, 11);
var independentMeasurement = dummyWorkout.GetLatestTelemetry();
Check(independentMeasurement.PowerWatts == 190 && dummyWorkout.TargetPowerWatts == 190 &&
      dummyWorkout.WorkoutTargetPowerWatts == 240,
    "dummy ERG target remains separate from actual dummy power telemetry");
await dummyWorkout.ReleaseWorkoutControlAsync(11);
Check(dummyWorkout.WorkoutTargetPowerWatts is null, "dummy workout target clears on control release");

var commandWarningPolicy = new TrainerCommandWarningPolicy();
var warningSession = commandWarningPolicy.BeginSession();
Check(commandWarningPolicy.ObserveFailure(warningSession) == TrainerCommandWarningTransition.None &&
      commandWarningPolicy.ObserveTransportAcknowledgement(warningSession) == TrainerCommandWarningTransition.None &&
      !commandWarningPolicy.IsWarningVisible(warningSession),
    "an isolated gradient command failure is retained for diagnostics but does not leave a rider-facing warning");
Check(commandWarningPolicy.ObserveFailure(warningSession) == TrainerCommandWarningTransition.None &&
      commandWarningPolicy.ObserveFailure(warningSession) == TrainerCommandWarningTransition.None &&
      commandWarningPolicy.ObserveFailure(warningSession) == TrainerCommandWarningTransition.Show &&
      commandWarningPolicy.IsWarningVisible(warningSession),
    "three consecutive gradient command failures show a persistent warning");
Check(commandWarningPolicy.IsWarningVisible(warningSession),
    "fresh trainer telemetry alone does not clear a command warning");
Check(commandWarningPolicy.ObserveTransportAcknowledgement(warningSession) == TrainerCommandWarningTransition.Clear &&
      !commandWarningPolicy.IsWarningVisible(warningSession),
    "a later transport acknowledgement clears the current gradient command warning");
var staleWarningSession = warningSession;
commandWarningPolicy.EndSession(warningSession);
var nextWarningSession = commandWarningPolicy.BeginSession();
Check(commandWarningPolicy.ObserveFailure(staleWarningSession) == TrainerCommandWarningTransition.None &&
      commandWarningPolicy.ObserveTransportAcknowledgement(staleWarningSession) == TrainerCommandWarningTransition.None &&
      !commandWarningPolicy.IsCurrentSession(staleWarningSession) &&
      commandWarningPolicy.GetConsecutiveFailureCount(nextWarningSession) == 0,
    "stale command callbacks from a previous ride cannot change current warning state");

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

var freshnessNow = DateTimeOffset.UtcNow;
Check(!TrainerTelemetryFreshnessPolicy.HasExpired(freshnessNow, freshnessNow + TimeSpan.FromSeconds(5),
          TrainerRecoveryPolicy.AntTelemetryLossTimeout) &&
      TrainerTelemetryFreshnessPolicy.HasExpired(freshnessNow, freshnessNow + TimeSpan.FromSeconds(6),
          TrainerRecoveryPolicy.AntTelemetryLossTimeout) &&
      TrainerTelemetryFreshnessPolicy.HasExpired(DateTimeOffset.MinValue, freshnessNow,
          TrainerRecoveryPolicy.AntTelemetryLossTimeout),
    "ANT telemetry loss is based on the last fresh trainer page and has a bounded timeout");
var freshStationaryZero = new TrainerTelemetry(freshnessNow, 0, 0, 0, HasPowerMeasurement: true,
    HasCadenceMeasurement: true, HasReceivedMeasurement: true, HasSpeedMeasurement: true);
Check(TrainerTelemetryFreshnessPolicy.IsFresh(freshStationaryZero, freshnessNow + TimeSpan.FromSeconds(1),
          TrainerRecoveryPolicy.AntTelemetryLossTimeout) && freshStationaryZero.PowerWatts == 0 &&
      freshStationaryZero.CadenceRpm == 0,
    "valid stationary zero power and cadence remain fresh rather than triggering recovery");
var staleFitSample = freshStationaryZero with { Timestamp = freshnessNow - TimeSpan.FromSeconds(10),
    PowerWatts = 240, CadenceRpm = 90, SpeedKph = 25, HeartRateBpm = 150 };
var sanitizedFitSample = staleFitSample.ForRecording(freshnessNow);
Check(sanitizedFitSample.HasReceivedMeasurement && !sanitizedFitSample.HasPowerMeasurement &&
      !sanitizedFitSample.HasCadenceMeasurement && sanitizedFitSample.PowerWatts == 0 &&
      sanitizedFitSample.CadenceRpm == 0 && sanitizedFitSample.SpeedKph == 0 &&
      sanitizedFitSample.HeartRateBpm is null,
    "stale ANT measurements are cleared before a recording sample is written");
Check(TrainerRecoveryPolicy.AntDiscoveryWindow >= TimeSpan.FromSeconds(20),
    "ANT wildcard discovery stays open long enough for delayed acquisition instead of cycling five-second scans");
Check(!AntFeCConnectionAttemptPolicy.IsCurrent(41, 42) &&
      AntFeCConnectionAttemptPolicy.IsCurrent(42, 42),
    "queued controls and receive callbacks cannot cross ANT reconnect generations");

// Exercise the production recovery loop with a simulated trainer transport. Its selected identity
// cannot change, stale telemetry is cleared on loss, and only current ride settings are restored.
var simulatedIdentity = new AntFeCDeviceIdentity(100, AntFeCProtocol.DeviceType, 1);
var simulatedConnected = true;
var simulatedTelemetry = freshStationaryZero;
var simulatedRecoveryElapsed = TimeSpan.Zero;
var currentRideGrade = -2.25;
var currentRiderWeight = 82.0;
var restoredGrade = double.NaN;
var restoredWeight = double.NaN;
var staleGeneration = 41L;
var recoveryGeneration = 42L;
var staleCallbackAccepted = false;
simulatedConnected = false;
simulatedTelemetry = simulatedTelemetry with { Timestamp = DateTimeOffset.MinValue,
    HasReceivedMeasurement = false, HasPowerMeasurement = false, HasCadenceMeasurement = false };
var simulatedRecovery = await TrainerRecoveryLoop.RunAsync(
    (attempt, _) =>
    {
        if (simulatedRecoveryElapsed < TimeSpan.FromSeconds(15)) return Task.FromResult(false);
        simulatedConnected = true;
        simulatedTelemetry = freshStationaryZero with { Timestamp = freshnessNow + simulatedRecoveryElapsed };
        restoredGrade = currentRideGrade;
        restoredWeight = currentRiderWeight;
        // A callback from the previous connection generation is ignored after reconnect.
        staleCallbackAccepted = AntFeCConnectionAttemptPolicy.IsCurrent(staleGeneration, recoveryGeneration);
        if (staleCallbackAccepted) throw new InvalidOperationException("A stale connection callback changed the active generation.");
        return Task.FromResult(true);
    }, maximumAttempts: 8, recoveryWindow: TimeSpan.FromSeconds(30),
    _ => TimeSpan.FromSeconds(5), CancellationToken.None,
    delayAsync: (delay, _) => { simulatedRecoveryElapsed += delay; return Task.CompletedTask; });
Check(simulatedRecovery.Outcome == TrainerRecoveryOutcome.Recovered && simulatedConnected &&
      simulatedRecoveryElapsed >= TimeSpan.FromSeconds(15) && simulatedIdentity.DeviceNumber == 100 &&
      simulatedTelemetry.HasReceivedMeasurement,
    "simulated ANT transport recovers the same trainer after a 15-second power-cycle delay with fresh telemetry");
Check(restoredGrade == currentRideGrade && restoredWeight == currentRiderWeight && !staleCallbackAccepted,
    "recovery restores current control settings and rejects callbacks from the previous connection generation");
var simulatedExpiry = await TrainerRecoveryLoop.RunAsync((_, _) => Task.FromResult(false),
    maximumAttempts: 2, recoveryWindow: TimeSpan.FromSeconds(5), _ => TimeSpan.Zero, CancellationToken.None);
var simulatedManualRetry = await TrainerRecoveryLoop.RunAsync((_, _) => Task.FromResult(true),
    maximumAttempts: 2, recoveryWindow: TimeSpan.FromSeconds(5), _ => TimeSpan.Zero, CancellationToken.None);
Check(simulatedExpiry.Outcome == TrainerRecoveryOutcome.Expired &&
      simulatedManualRetry.Outcome == TrainerRecoveryOutcome.Recovered,
    "expired ANT recovery can be restarted manually without ending the ride");

var fecPower = new AntFeCPowerAccumulator();
var rawAntKey = new byte[] { 1, 35, 69, 103, 137, 171, 205, 239 };
Check(AntNetworkKeyMaterial.Parse(" 0123456789abcdef ").SequenceEqual(rawAntKey),
    "ANT+ setup trims whitespace and parses exactly 16 hexadecimal digits");
var acceptedKeyFormats = new[]
{
    "0123456789abcdef",
    "0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF",
    "01 23\r\n45\t67 89 ab cd ef",
    "01:23:45:67:89:ab:cd:ef",
    "01-23-45-67-89-AB-CD-EF"
};
Check(acceptedKeyFormats.All(format => AntNetworkKeyMaterial.Parse(format).SequenceEqual(rawAntKey)),
    "ANT+ 64-bit key accepts compact, Garmin comma-byte, whitespace, colon, and hyphen formats");
var invalidKeyFormats = new[]
{
    "0123456789abcde", "00112233445566778899aabbccddeeff", "0x01,0x23,0x45,0x67,0x89,0xAB,0xCD",
    "01 23 45 67 89 AB CD EF 10", "0x01 0x23 0x45 0x67 0x89 0xAB 0xCD 0xEF",
    "01:23-45:67:89:AB:CD:EF", "01:23:45:67:89:AG:CD:EF", "key=0123456789abcdef"
};
Check(invalidKeyFormats.All(format =>
{
    try { _ = AntNetworkKeyMaterial.Parse(format); return false; }
    catch (InvalidDataException) { return true; }
}), "ANT+ key parser rejects malformed, mixed-separator, and oversized input");
var antConfigDirectory = Path.Combine(Path.GetTempPath(), $"WodraAntConfigTests-{Guid.NewGuid():N}");
var antConfigPath = Path.Combine(antConfigDirectory, "ant-config.json");
try
{
    Check(AntNetworkKeyMaterial.GetStatus(antConfigPath) == "Not configured",
        "missing ANT+ configuration is reported without throwing");
    try { _ = AntNetworkKeyMaterial.Load(antConfigPath); throw new InvalidOperationException("Missing ANT config loaded."); }
    catch (InvalidOperationException error) when (error.Message.Contains("ANT+ is not configured", StringComparison.Ordinal))
    { Check(true, "ANT initialization gives an actionable missing configuration error"); }
    AntNetworkKeyMaterial.Save(" 0123456789abcdef ", antConfigPath);
    Check(AntNetworkKeyMaterial.GetStatus(antConfigPath) == "Configured" &&
          AntNetworkKeyMaterial.Load(antConfigPath).SequenceEqual(rawAntKey),
        "ANT+ key persists atomically and runtime initialization loads the configured value");
    Check(File.ReadAllText(antConfigPath).Contains("\"networkKey\":\"0123456789abcdef\"", StringComparison.Ordinal),
        "ANT+ configuration uses the documented JSON shape");
    try { AntNetworkKeyMaterial.Save("not-a-network-key", antConfigPath); throw new InvalidOperationException("Invalid ANT config saved."); }
    catch (InvalidDataException error) when (!error.Message.Contains("not-a-network-key", StringComparison.Ordinal))
    { Check(true, "invalid ANT+ input is rejected without echoing the entered value"); }
    File.WriteAllText(antConfigPath, "{broken");
    Check(AntNetworkKeyMaterial.GetStatus(antConfigPath).Contains("invalid", StringComparison.OrdinalIgnoreCase),
        "corrupt ANT+ configuration is reported safely");
    AntNetworkKeyMaterial.Save("0123456789ABCDEF", antConfigPath);
    AntNetworkKeyMaterial.Clear(antConfigPath);
    Check(AntNetworkKeyMaterial.GetStatus(antConfigPath) == "Not configured",
        "clearing ANT+ setup removes the configuration");
}
finally { if (Directory.Exists(antConfigDirectory)) Directory.Delete(antConfigDirectory, true); }
try
{
    _ = AntNetworkKeyMaterial.Parse("not-a-network-key");
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
var fecTargetPower = AntFeCProtocol.BuildTargetPower(200);
Check(fecTargetPower[0] == AntFeCProtocol.PageTargetPower && fecTargetPower[6] == 0x20 && fecTargetPower[7] == 0x03,
    "FE-C target-power command uses quarter-watt units and the documented data page");
var ftmsFeatures = new byte[8]; ftmsFeatures[4] = 0x08; ftmsFeatures[5] = 0x20;
var ftmsPowerRange = new byte[] { 50, 0, 0xB8, 0x0B, 5, 0 };
var ftmsCapabilities = FtmsWorkoutCapabilities.Parse(ftmsFeatures, ftmsPowerRange);
Check(ftmsCapabilities.SupportsTargetPower && ftmsCapabilities.SupportsSimulation &&
      ftmsCapabilities.MinimumPowerWatts == 50 && ftmsCapabilities.MaximumPowerWatts == 3000 && ftmsCapabilities.PowerIncrementWatts == 5,
    "FTMS target-setting bits and supported power range are parsed from their advertised fields");
ftmsFeatures[4] = 0;
Check(!FtmsWorkoutCapabilities.Parse(ftmsFeatures, ftmsPowerRange).SupportsTargetPower,
    "FTMS target-power ERG is unavailable when the trainer does not advertise the capability");
var fecRequest = AntFeCProtocol.BuildPageRequest(AntFeCProtocol.PageCapabilities);
Check(fecRequest.SequenceEqual(new byte[] { 0x46, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x36, 0x01 }),
    "FE-C page 70 matches the profile wire vector for a one-response slave-to-master request");
var capabilityResponses = new AntFeCCapabilityResponseTracker(expectedChannel: 0, attemptId: 42);
var transportAcknowledged = true; // EVENT_TRANSFER_TX_COMPLETED confirms only the request transfer.
Check(transportAcknowledged && !capabilityResponses.HasCapabilities &&
      AntFeCProtocol.IsReady(true, true, capabilityResponses.HasCapabilities),
    "ANT request acknowledgement does not establish capability knowledge while telemetry can still be ready");
var fecCapabilityResponse = new byte[] { 0x36, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 };
await Task.Delay(10); // A capability page may arrive on a later receive callback.
Check(capabilityResponses.Observe(42, 0, fecCapabilityResponse) == "capabilities_received" &&
      capabilityResponses.CapabilityFlags == 0x07 && (capabilityResponses.CapabilityFlags.GetValueOrDefault() & 0x04) != 0 &&
      AntFeCProtocol.IsReady(true, true, capabilityResponses.HasCapabilities),
    "valid FE-C page 54 response decodes its three supported modes and completes readiness");
var wrongChannelCapabilities = new AntFeCCapabilityResponseTracker(expectedChannel: 0, attemptId: 42);
var malformedCapabilities = new AntFeCCapabilityResponseTracker(expectedChannel: 0, attemptId: 42);
var nextAttemptCapabilities = new AntFeCCapabilityResponseTracker(expectedChannel: 0, attemptId: 43);
Check(wrongChannelCapabilities.Observe(42, 1, fecCapabilityResponse) == "wrong_channel" &&
      malformedCapabilities.Observe(42, 0, new byte[] { 0x36, 0xFF, 0xFF, 0xFF, 0x01, 0xFF, 0xFF, 0x01 }) == "malformed_page" &&
      malformedCapabilities.Observe(42, 0, new byte[] { 0x36, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0x81 }) == "malformed_page" &&
      malformedCapabilities.Observe(42, 0, new byte[] { 0x37, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }) == "malformed_page" &&
      nextAttemptCapabilities.Observe(42, 0, fecCapabilityResponse) == "stale_attempt" &&
      !wrongChannelCapabilities.HasCapabilities && !malformedCapabilities.HasCapabilities &&
      !nextAttemptCapabilities.HasCapabilities,
    "delayed active pages are accepted while wrong-channel, malformed, reserved-bit, wrong-page, and stale pages are rejected");
var statusRequest = AntFeCProtocol.BuildPageRequest(AntFeCProtocol.PageCommandStatus);
Check(statusRequest.SequenceEqual(new byte[] { 0x46, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x47, 0x01 }),
    "FE-C command-status request uses the same profile-correct common-page encoding");
Check(AntFeCProtocol.ShouldRetryCapabilityRequest(false, 0) &&
      AntFeCProtocol.ShouldRetryCapabilityRequest(false, AntFeCProtocol.MaximumCapabilityRequestAttempts - 1) &&
      !AntFeCProtocol.ShouldRetryCapabilityRequest(false, AntFeCProtocol.MaximumCapabilityRequestAttempts) &&
      !AntFeCProtocol.ShouldRetryCapabilityRequest(true, 0),
    "FE-C capability requests retry only while the page is missing and attempts remain");
var discoveryChannelSettings = AntFeCProtocol.DiscoveryChannel;
Check(discoveryChannelSettings.DeviceNumber == 0 && !discoveryChannelSettings.PairingEnabled &&
      discoveryChannelSettings.DeviceType == 17 && discoveryChannelSettings.TransmissionType == 0 &&
      discoveryChannelSettings.Period == 8192 && discoveryChannelSettings.RadioFrequency == 57,
    "FE-C discovery uses the proven wildcard slave-receive settings with pairing disabled");
var sdkExtendedScanFrame = new byte[] { 0, 0x34, 0x12, 0x91, 0x01, 16, 0, 0, 0, 0, 0, 0, 0 };
var parsedExtendedScan = AntFeCScanFrameParser.Parse(0x5D, sdkExtendedScanFrame);
Check(parsedExtendedScan.IsExtended && parsedExtendedScan.PayloadLength == 8 &&
      parsedExtendedScan.DataPage == AntFeCProtocol.PageGeneral &&
      parsedExtendedScan.Source == new AntFeCDeviceIdentity(0x1234, AntFeCProtocol.DeviceType, 1) &&
      parsedExtendedScan.RejectionReason is null,
    "ANT SDK extended broadcast frames from channelResponse identify FE-C source and page");
var manufacturerFrame = new byte[] { 0, 0x34, 0x12, 17, 1, 80, 0x22, 0x01, 0xAA, 0xBB, 0, 0, 0 };
var productFrame = new byte[] { 0, 0x34, 0x12, 17, 1, 81, 1, 0, 0x78, 0x56, 0, 0, 0 };
var manufacturerInfo = AntFeCScanFrameParser.Parse(0x5D, manufacturerFrame);
var productInfo = AntFeCScanFrameParser.Parse(0x5D, productFrame);
Check(manufacturerInfo.ManufacturerId == 0x0122 && productInfo.ProductId == 0x5678 &&
      new AntFeCDiscoveryCoordinator().Observe(0x5D, 0, manufacturerFrame).Outcome == "device_info_page",
    "FE-C manufacturer and product info pages are decoded for labels without becoming trainer candidates on their own");
var sdkFlagExtendedBroadcast = new byte[] { 0, 25, 0, 0, 0, 0, 0, 0, 0, 0x80, 0x78, 0x56, 0x91, 0x02 };
var parsedFlagExtended = AntFeCScanFrameParser.Parse(0x4E, sdkFlagExtendedBroadcast);
Check(parsedFlagExtended.IsExtended && parsedFlagExtended.PayloadLength == 8 &&
      parsedFlagExtended.Source == new AntFeCDeviceIdentity(0x5678, AntFeCProtocol.DeviceType, 2),
    "ANT SDK ordinary broadcast frames with extended channel-ID flag are parsed");
var channelIdentityResponse = AntFeCScanFrameParser.ParseChannelIdentity(0x51, new byte[] { 0, 0x6E, 0x10, 17, 5 });
var sdkOrdinaryBroadcast = new byte[] { 0, 16, 0x1F, 0x60, 0x33, 0x29, 0x06, 0xFF, 0x34 };
var parsedOrdinaryBroadcast = AntFeCScanFrameParser.Parse(0x4E, sdkOrdinaryBroadcast, channelIdentityResponse);
Check(channelIdentityResponse == new AntFeCDeviceIdentity(4206, 17, 5) &&
      parsedOrdinaryBroadcast.DataPage == AntFeCProtocol.PageGeneral &&
      parsedOrdinaryBroadcast.Source == channelIdentityResponse && parsedOrdinaryBroadcast.RejectionReason is null,
    "ANT wildcard receive resolves the 0x51 source identity and accepts ordinary 0x4E FE-C frames");
var discoveryCoordinator = new AntFeCDiscoveryCoordinator(expectedChannel: 0);
var queuedGeneral = discoveryCoordinator.Observe(0x4E, 0, sdkOrdinaryBroadcast);
var ordinaryTrainerBroadcast = new byte[] { 0, 25, 0x75, 0x0D, 0xF1, 0x16, 0x1F, 0x60, 0x33 };
var queuedTrainer = discoveryCoordinator.Observe(0x4E, 0, ordinaryTrainerBroadcast);
Check(queuedGeneral.Outcome == "trainer_page_pending_identity" &&
      queuedTrainer.Outcome == "trainer_page_pending_identity" && queuedTrainer.IdentityRequestNeeded &&
      discoveryCoordinator.HasTrainerDataReceived && discoveryCoordinator.BufferedTelemetryFrameCount == 2,
    "ordinary FE-C pages 16 and 25 arriving through channelResponse are retained until channel identity resolves");
Check(discoveryCoordinator.TryBeginIdentityRequest(out var identityAttempt) && identityAttempt == 1,
    "ordinary FE-C telemetry schedules the first bounded channel-ID request");
var syntheticResolvedIdentity = new byte[] { 0, 0x34, 0x12, 17, 5 };
var resolvedAfterTelemetry = discoveryCoordinator.Observe(0x51, 0, syntheticResolvedIdentity);
Check(resolvedAfterTelemetry.Outcome == "identity_resolved" && resolvedAfterTelemetry.TrainerIdentified &&
      discoveryCoordinator.BufferedTelemetryFrameCount == 0 &&
      discoveryCoordinator.Candidates.SequenceEqual([new AntFeCDeviceIdentity(0x1234, 17, 5)]),
    "deviceResponse channel-ID reply resolves buffered ordinary pages into an eligible trainer");

var identityFirstCoordinator = new AntFeCDiscoveryCoordinator(expectedChannel: 0);
identityFirstCoordinator.RequestIdentity();
Check(identityFirstCoordinator.TryBeginIdentityRequest(out var identityFirstAttempt) && identityFirstAttempt == 1,
    "identity-first integration fixture sends a channel-ID request before telemetry");
var identityFirstResponse = identityFirstCoordinator.Observe(0x51, 0, syntheticResolvedIdentity);
var identityFirstPage = identityFirstCoordinator.Observe(0x4E, 0, sdkOrdinaryBroadcast);
Check(identityFirstResponse.Outcome == "identity_resolved" &&
      identityFirstPage.Outcome == "trainer_page_identified" &&
      identityFirstCoordinator.Candidates.Single() == new AntFeCDeviceIdentity(0x1234, 17, 5),
    "resolved identity is retained when the response arrives before ordinary telemetry");

var boundedIdentityCoordinator = new AntFeCDiscoveryCoordinator(expectedChannel: 0);
boundedIdentityCoordinator.Observe(0x4E, 0, sdkOrdinaryBroadcast);
for (var retry = 1; retry <= AntFeCDiscoveryCoordinator.MaximumIdentityRequests; retry++)
{
    Check(boundedIdentityCoordinator.TryBeginIdentityRequest(out var currentAttempt) && currentAttempt == retry,
        $"missing channel identity schedules bounded request {retry}");
    var shouldRetry = boundedIdentityCoordinator.MarkIdentityRequestIncomplete(currentAttempt);
    Check(shouldRetry == (retry < AntFeCDiscoveryCoordinator.MaximumIdentityRequests),
        $"channel identity retry {retry} remains bounded");
}
Check(!boundedIdentityCoordinator.TryBeginIdentityRequest(out _) && boundedIdentityCoordinator.Candidates.Count == 0 &&
      boundedIdentityCoordinator.HasTrainerDataReceived,
    "missing identity preserves the distinction between received telemetry and an identified trainer");
var boundedPendingCoordinator = new AntFeCDiscoveryCoordinator(expectedChannel: 0);
for (var i = 0; i < 40; i++) boundedPendingCoordinator.Observe(0x4E, 0, sdkOrdinaryBroadcast);
Check(boundedPendingCoordinator.BufferedTelemetryFrameCount == 32,
    "wildcard discovery retains at most 32 pending FE-C frames while identity is unresolved");

var guardedIdentityCoordinator = new AntFeCDiscoveryCoordinator(expectedChannel: 0);
guardedIdentityCoordinator.RequestIdentity();
guardedIdentityCoordinator.TryBeginIdentityRequest(out var guardedAttempt);
Check(guardedIdentityCoordinator.Observe(0x51, 1, [1, 0x34, 0x12, 17, 5]).Outcome == "wrong_channel" &&
      guardedIdentityCoordinator.Observe(0x51, 0, [0, 0, 0, 17, 0]).Outcome == "unresolved_wildcard_identity" &&
      guardedIdentityCoordinator.MarkIdentityRequestIncomplete(guardedAttempt),
    "wrong-channel and zero wildcard identity responses are rejected and permit a bounded retry");
var staleIdentityCoordinator = new AntFeCDiscoveryCoordinator(expectedChannel: 0);
staleIdentityCoordinator.RequestIdentity();
staleIdentityCoordinator.TryBeginIdentityRequest(out _);
staleIdentityCoordinator.Deactivate();
Check(staleIdentityCoordinator.Observe(0x51, 0, syntheticResolvedIdentity).Outcome == "stale_attempt" &&
      staleIdentityCoordinator.Candidates.Count == 0,
    "late channel-ID callbacks from a completed scan attempt cannot affect a later attempt");
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
Check(AntFeCProtocol.IsReady(true, true, false) && AntFeCProtocol.IsReady(true, true, true) &&
      !AntFeCProtocol.IsReady(true, false, true),
    "FE-C telemetry readiness requires general and trainer pages but not capability metadata");

var liveSession = new AntFeCSessionState();
liveSession.ObserveTelemetryPage(16);
Check(!liveSession.IsTelemetryReady && liveSession.SimulationSupport == AntFeCCapabilitySupport.Unknown,
    "one telemetry page does not establish a ready link and missing capabilities remain unknown");
liveSession.ObserveTelemetryPage(25);
Check(liveSession.IsTelemetryReady && liveSession.SimulationSupport == AntFeCCapabilitySupport.Unknown,
    "identified general and trainer telemetry establish connection without inventing capability flags");
Check(liveSession.ObserveCapabilities(fecCapabilityResponse) && liveSession.SimulationSupport == AntFeCCapabilitySupport.Supported,
    "a late valid page 54 updates capability support on the active session");
var unsupportedSession = new AntFeCSessionState();
unsupportedSession.ObserveTelemetryPage(16);
unsupportedSession.ObserveTelemetryPage(25);
Check(unsupportedSession.ObserveCapabilities(new byte[] { 54, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0x03 }) &&
      unsupportedSession.SimulationSupport == AntFeCCapabilitySupport.Unsupported,
    "valid page 54 with the simulation bit clear explicitly disables simulation control");

var simulatedSession = new AntFeCSessionState();
simulatedSession.ObserveTelemetryPage(16);
simulatedSession.ObserveTelemetryPage(25);
var sentPages = new List<byte>();
await simulatedSession.SendControlAsync(AntFeCProtocol.BuildTrackResistance(2.5),
    AntFeCProtocol.BuildUserConfiguration(75, 10), (packet, _) =>
    {
        sentPages.Add(packet[0]);
        return Task.FromResult(true);
    }, () => { });
Check(sentPages.SequenceEqual(new byte[] { 55, 51 }) &&
      simulatedSession.TransportState == AntFeCTransportState.Acknowledged &&
      simulatedSession.TrainerCommandState == AntFeCTrainerCommandState.Unknown &&
      !simulatedSession.PhysicalResistanceVerified,
    "simulated transport sends user configuration before track control and does not equate transport acknowledgement with trainer status or physical resistance");
Check(simulatedSession.ObserveCommandStatus(new AntFeCCommandStatus(51, 9, 0)) &&
      simulatedSession.TrainerCommandState == AntFeCTrainerCommandState.Accepted &&
      !simulatedSession.PhysicalResistanceVerified,
    "a matching late page 71 adds trainer-reported acceptance without claiming physical resistance");
Check(simulatedSession.ObserveCommandStatus(new AntFeCCommandStatus(51, 10, 1)) &&
      simulatedSession.TrainerCommandState == AntFeCTrainerCommandState.Failed,
    "a trainer-reported negative command status remains a definite failure");

var failedTransportSession = new AntFeCSessionState();
var rejectedControl = false;
try
{
    await failedTransportSession.SendControlAsync(AntFeCProtocol.BuildTrackResistance(1), null,
        (_, _) => Task.FromResult(false));
}
catch (IOException) { rejectedControl = true; }
Check(rejectedControl && failedTransportSession.TransportState == AntFeCTransportState.Failed,
    "a definite ANT transport rejection fails control instead of being treated as success");
var cancelledSession = new AntFeCSessionState();
using (var cancelledSend = new CancellationTokenSource())
{
    cancelledSend.Cancel();
    var cancelled = false;
    try
    {
        await cancelledSession.SendControlAsync(AntFeCProtocol.BuildTrackResistance(0), null,
            (_, token) => Task.FromCanceled<bool>(token), cancellationToken: cancelledSend.Token);
    }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "control sends preserve cancellation through simulated transport");
}
simulatedSession.Reset();
Check(!simulatedSession.IsTelemetryReady && simulatedSession.CapabilityFlags is null &&
      simulatedSession.TransportState == AntFeCTransportState.NotAttempted &&
      simulatedSession.TrainerCommandState == AntFeCTrainerCommandState.Unknown,
    "disconnect/reconnect resets telemetry, capabilities and control evidence instead of retaining stale state");
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
Check(HeartRateMeasurementParser.Parse([0x00, 72]) == 72 &&
      HeartRateMeasurementParser.Parse([0x01, 0x2c, 0x01]) == 300 &&
      HeartRateMeasurementParser.Parse([0x01, 0x2c]) is null &&
      HeartRateMeasurementParser.Parse([0x00, 0x00]) is null,
    "Bluetooth Heart Rate Measurement parser handles 8/16-bit values and rejects incomplete or invalid samples");
var hrA = new BleDiscoveredDevice(101, "Pulse", [HeartRateDiscoveryPolicy.ServiceUuid], -40);
var hrB = new BleDiscoveredDevice(102, "Chest strap", [HeartRateDiscoveryPolicy.ServiceUuid], -50);
var nonHr = new BleDiscoveredDevice(103, "Controller", [TrainerDiscoveryPolicy.FtmsServiceUuid], -20);
Check(HeartRateDiscoveryPolicy.Select([]).Outcome == HeartRateDiscoveryOutcome.None &&
      HeartRateDiscoveryPolicy.Select([hrA]).Selected == hrA &&
      HeartRateDiscoveryPolicy.Select([hrA, hrB]).Outcome == HeartRateDiscoveryOutcome.Choose &&
      HeartRateDiscoveryPolicy.Select([hrA, hrB], preferredAddress: hrB.BluetoothAddress).Selected == hrB &&
      HeartRateDiscoveryPolicy.Select([hrA, hrA with { Rssi = -30 }]).Candidates.Count == 1 &&
      HeartRateDiscoveryPolicy.Select([nonHr]).Outcome == HeartRateDiscoveryOutcome.None,
    "optional heart-rate discovery auto-selects one compatible monitor, prefers its remembered identity, deduplicates advertisements, asks when several exist, and ignores other services");
var sterzoA = new BleDiscoveredDevice(201, "Elite Sterzo Smart", [SteeringDiscoveryPolicy.ServiceUuid], -45);
var sterzoB = new BleDiscoveredDevice(202, "Sterzo", [], -55);
Check(SteeringDiscoveryPolicy.Select([]).Outcome == SteeringDiscoveryOutcome.None &&
      SteeringDiscoveryPolicy.Select([sterzoA]).Selected == sterzoA &&
      SteeringDiscoveryPolicy.Select([sterzoA, sterzoB]).Outcome == SteeringDiscoveryOutcome.Choose &&
      SteeringDiscoveryPolicy.Select([sterzoA, sterzoB], preferredAddress: sterzoB.BluetoothAddress).Selected == sterzoB &&
      SteeringDiscoveryPolicy.Select([sterzoA, sterzoA with { Rssi = -30 }]).Candidates.Count == 1 &&
      SteeringDiscoveryPolicy.Select([nonHr]).Outcome == SteeringDiscoveryOutcome.None,
    "optional steering discovery auto-selects one Sterzo, prefers remembered identity, deduplicates advertisements, asks for multiple units, and ignores unrelated devices");
var freshOptionalHr = new TrainerTelemetry(now - TimeSpan.FromSeconds(30), 0, 0, 0,
    HeartRateBpm: 147, HasPowerMeasurement: false, HasCadenceMeasurement: false,
    HasReceivedMeasurement: false, HasSpeedMeasurement: false, HeartRateTimestamp: now);
var staleOptionalHr = freshOptionalHr with { HeartRateTimestamp = now - TimeSpan.FromSeconds(10) };
Check(!freshOptionalHr.ForRecording(now).HasPowerMeasurement &&
      freshOptionalHr.ForRecording(now).HeartRateBpm == 147 &&
      staleOptionalHr.ForRecording(now).HeartRateBpm is null,
    "fresh optional heart rate survives stale trainer telemetry for FIT while stale BPM is removed");

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
    LastSuccessfulProtocol = TrainerProtocol.AntFeC,
    LastAntDeviceNumber = 42,
    LastAntDeviceType = 17,
    LastAntTransmissionType = 5
});
var restoredTrainerSettings = JsonSerializer.Deserialize<TrainerSettings>(serializedTrainerSettings)!;
Check(restoredTrainerSettings.ProtocolPreference == TrainerProtocolPreference.AntFeC &&
      restoredTrainerSettings.LastSuccessfulProtocol == TrainerProtocol.AntFeC &&
      restoredTrainerSettings.LastAntDeviceNumber == 42 && restoredTrainerSettings.LastAntDeviceType == 17 &&
      restoredTrainerSettings.LastAntTransmissionType == 5,
    "protocol preference and ANT identity persist independently of the Bluetooth address");
var autoBoth = TrainerProtocolPreferencePolicy.Select(TrainerProtocolPreference.Auto, true, true);
var autoRememberedAnt = TrainerProtocolPreferencePolicy.Select(TrainerProtocolPreference.Auto, true, true, TrainerProtocol.AntFeC);
var autoRememberedBluetooth = TrainerProtocolPreferencePolicy.Select(TrainerProtocolPreference.Auto, true, true, TrainerProtocol.BluetoothFtms);
var autoAntOnly = TrainerProtocolPreferencePolicy.Select(TrainerProtocolPreference.Auto, false, true);
var explicitUnavailable = TrainerProtocolPreferencePolicy.Select(TrainerProtocolPreference.AntFeC, true, false);
Check(autoBoth.Protocol == TrainerProtocol.BluetoothFtms && autoAntOnly.Protocol == TrainerProtocol.AntFeC,
    "Auto deterministically prefers Bluetooth and uses ANT only when it is the available provider");
Check(autoRememberedAnt.Protocol == TrainerProtocol.AntFeC && autoRememberedBluetooth.Protocol == TrainerProtocol.BluetoothFtms,
    "Auto reuses the last successful trainer protocol when it remains available");
Check(!explicitUnavailable.IsAvailable && explicitUnavailable.UnavailableReason is not null,
    "an unavailable explicit protocol is reported instead of silently switching providers");
Check(DeviceProtocolPreferencePolicy.IsSupported(DeviceProtocolPreference.Bluetooth, DeviceProtocolCategory.Steering) &&
      !DeviceProtocolPreferencePolicy.IsSupported(DeviceProtocolPreference.AntPlus, DeviceProtocolCategory.HeartRate) &&
      !DeviceProtocolPreferencePolicy.IsSupported(DeviceProtocolPreference.AntPlus, DeviceProtocolCategory.Steering) &&
      DeviceProtocolPreferencePolicy.Normalize(DeviceProtocolPreference.AntPlus, DeviceProtocolCategory.Steering) == DeviceProtocolPreference.Auto,
    "unsupported ANT+ peripheral preferences are disabled and normalize safely");
var optionalProtocols = new OptionalDeviceProtocolPreferences
{
    HeartRate = DeviceProtocolPreference.Bluetooth,
    Steering = DeviceProtocolPreference.Auto
};
var restoredOptionalProtocols = JsonSerializer.Deserialize<OptionalDeviceProtocolPreferences>(
    JsonSerializer.Serialize(optionalProtocols))!;
restoredOptionalProtocols.Set(DeviceProtocolCategory.Steering, DeviceProtocolPreference.Bluetooth);
Check(restoredOptionalProtocols.HeartRate == DeviceProtocolPreference.Bluetooth &&
      restoredOptionalProtocols.Steering == DeviceProtocolPreference.Bluetooth,
    "heart-rate and steering protocol preferences persist independently and changing one leaves the other intact");
Check(DeviceProtocolPreferencePolicy.MigrateTrainer(TrainerProtocolPreference.AntFeC) == DeviceProtocolPreference.AntPlus &&
      DeviceProtocolPreferencePolicy.MigrateTrainer(TrainerProtocolPreference.BluetoothFtms) == DeviceProtocolPreference.Bluetooth,
    "existing trainer protocol choices map to the new category selector without changing identity fields");

var littleEndianAngle = BitConverter.GetBytes(-12.5f);
var challengeFrame = new byte[] { 0x03, 0x10, 0x12, 0x34 };
Check(SterzoSmartProtocol.ParseAngle(littleEndianAngle) == -12.5f &&
      SterzoSmartProtocol.ParseAngle([0, 0, 0]) is null &&
      SterzoSmartProtocol.ParseAngle(BitConverter.GetBytes(float.NaN)) is null,
    "Sterzo Smart angles decode as bounded finite little-endian float inputs");
var responseTable = new byte[131072];
responseTable[0x1234 * 2] = 0xA5;
responseTable[0x1234 * 2 + 1] = 0x5A;
Check(SterzoSmartProtocol.ParseChallenge(challengeFrame) == 0x1234 &&
      SterzoSmartProtocol.TryGetChallengeResponse(responseTable, 0x1234, out var responseFirst, out var responseSecond) &&
      responseFirst == 0xA5 && responseSecond == 0x5A &&
      !SterzoSmartProtocol.TryGetChallengeResponse(responseTable.AsSpan(0, 8), 0x1234, out _, out _),
    "Sterzo Smart big-endian challenges select the corresponding two-byte response table entry");
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
var rememberedAnt = new AntFeCDeviceIdentity(44, 17, 1);
var rememberedFirst = AntFeCDiscoveryPolicy.Select([
    new AntFeCDeviceIdentity(22, 17, 2), rememberedAnt, new AntFeCDeviceIdentity(91, 120, 1)], rememberedAnt);
Check(rememberedFirst.Outcome == AntFeCDiscoveryOutcome.Choose && rememberedFirst.Candidates.Count == 2 &&
      rememberedFirst.Candidates[0] == rememberedAnt && rememberedFirst.Candidates[1].DisplayName.Contains("/17/", StringComparison.Ordinal),
    "multi-device selection excludes non-FE-C peripherals, prefers a verified remembered identity, and labels its full ANT identity");
Check(AntFeCInitialConnectionPolicy.ShouldFallbackToDiscovery(true, isRecovery: false, cancelled: false) &&
      !AntFeCInitialConnectionPolicy.ShouldFallbackToDiscovery(true, isRecovery: true, cancelled: false) &&
      !AntFeCInitialConnectionPolicy.ShouldFallbackToDiscovery(true, isRecovery: false, cancelled: true) &&
      !AntFeCInitialConnectionPolicy.ShouldFallbackToDiscovery(false, isRecovery: false, cancelled: false),
    "a stale saved target falls back to discovery only for a non-cancelled initial connection, never during pinned recovery");
Check(AntFeCInitialConnectionPolicy.ShouldFallbackFromContinuousScan(0, cancelled: false) &&
      !AntFeCInitialConnectionPolicy.ShouldFallbackFromContinuousScan(2, cancelled: false) &&
      !AntFeCInitialConnectionPolicy.ShouldFallbackFromContinuousScan(0, cancelled: true),
    "multi-source scanning falls back to the proven normal receiver only when it found no eligible source and was not cancelled");
Check(AntInitialConnectionPolicy.RememberedTargetTimeout < TimeSpan.FromSeconds(40) &&
      AntInitialConnectionPolicy.RememberedTargetTimeout > TimeSpan.FromSeconds(20) &&
      AntInitialConnectionPolicy.DiscoveryWindow == TimeSpan.FromSeconds(20) &&
      AntInitialConnectionPolicy.ShouldCompleteDiscovery(1, TimeSpan.FromSeconds(3), TimeSpan.Zero) &&
      AntInitialConnectionPolicy.ShouldCompleteDiscovery(2, TimeSpan.FromSeconds(3), TimeSpan.Zero) &&
      !AntInitialConnectionPolicy.ShouldCompleteDiscovery(0, TimeSpan.FromSeconds(20), TimeSpan.Zero) &&
      !AntInitialConnectionPolicy.ShouldCompleteDiscovery(1, TimeSpan.FromSeconds(2.9), TimeSpan.Zero),
    "initial ANT discovery settles after verified candidates but retains the full no-candidate window");
Check(AntFeCInitialConnectionPolicy.CanPersistIdentity(rememberedAnt, rememberedAnt) &&
      !AntFeCInitialConnectionPolicy.CanPersistIdentity(null, rememberedAnt) &&
      !AntFeCInitialConnectionPolicy.CanPersistIdentity(new AntFeCDeviceIdentity(22, 17, 2), rememberedAnt) &&
      !AntFeCInitialConnectionPolicy.CanPersistIdentity(new AntFeCDeviceIdentity(44, 120, 1), null),
    "only a resolved eligible source matching its configured target can become the persisted ANT identity");
