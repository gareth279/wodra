using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Models;
using System.Text.Json;

var tests = new (string Name, Action Run)[]
{
    ("Zero-power flat riding decelerates and settles at rest", ZeroPowerFlatDeceleration),
    ("Zero-power downhill riding accelerates when gravity exceeds losses", ZeroPowerDownhillCoasting),
    ("Uphill speed is lower than flat speed at equal power", UphillSlowerThanFlat),
    ("Higher power produces greater acceleration", HigherPowerAcceleratesMore),
    ("A 97 kg system starts at 200 W on a 10% climb", TenPercentHillStart),
    ("A rider starts at reasonable power on an 8% climb", EightPercentHillStart),
    ("Zero power on an uphill does not create motion", ZeroPowerUphillStart),
    ("Low-speed reference is configurable and speed is used above it", LaunchReferenceIsConfigurable),
    ("Speed never becomes negative", SpeedNeverNegative),
    ("Trainer speed telemetry does not determine game speed", TrainerSpeedIsIgnored),
	("HUD telemetry uses live ride state and simulated speed", HudMetricsUseRideState),
	("HUD marks stale rider sensors unavailable", HudMetricsMarkStaleSensors),
    ("Mass and bike resistance profile affect acceleration", ProfilesAffectPhysics),
    ("Ride pause freezes progress and resumes without a distance jump", RidePauseSemantics),
    ("Repeated ride pause action toggles explicit ride state", RidePauseToggle),
    ("Debug mode off emits no periodic snapshots", DebugModeOff),
    ("fallback building heights and roof profiles vary deterministically", BuildingFallbackVariation),
    ("procedural facade windows vary by OSM identity and remain repeatable", BuildingFacadeVariation),
    ("Debug snapshot contains ride state and calculated lap position", DebugSnapshotFields),
    ("Debug marker numbering increments", DebugMarkerNumbers),
    ("Screenshot filename and marker sidecar share collision-safe names", ScreenshotNames),
    ("Screenshot sidecar contains compact ride metadata", ScreenshotMetadataFields),
    ("F12 selects clean or visible HUD capture", ScreenshotModeSelection),
    ("Screenshot directory falls back when Pictures is unavailable", ScreenshotDirectoryFallback),
    ("Screenshot failure does not interrupt debug marker creation", ScreenshotFailureKeepsMarker),
    ("Debug distance jump preserves elapsed ride state and supports later laps", DebugDistanceJump)
    ,("Remote riders interpolate absolute lap progress and telemetry", RemoteRiderInterpolation)
    ,("Remote rider reposition clears the old interpolation path", RemoteRiderRepositionReset)
    ,("Stale remote rider tracks stop producing render positions", RemoteRiderStaleness)
    ,("FIT recording segment rebases route distance after catch-up", RecordingSegmentRebasesAfterTeleport)
    ,("Catch-up position stays behind absolute distance across laps and route seams", CatchUpRoutePosition)
	,("Human rider relative distance is signed and wraps at lap boundaries", SignedRemoteRiderDistance)
    ,("Catch-up matches a fresh remote speed and leaves subsequent motion to local physics", CatchUpMatchesFreshRemoteSpeed)
    ,("Catch-up speed falls back safely for stale invalid stationary and paused targets", CatchUpSpeedFallbacks)
    ,("Trainer difficulty scales signed grades and preserves actual ride physics", TrainerDifficultyScalesSignedGrades)
    ,("Catch-up grade transition is gradual and safely replaceable", CatchUpTrainerGradeTransition)
    ,("Paused catch-up retains matched resume speed without paused movement", PausedCatchUpResumeSpeed)
    ,("Startup online status stays compact for offline configured and unavailable states", StartupOnlineStatusIsCompact)
    ,("EOS lobby UI status distinguishes membership and P2P phases", EosLobbyStatusPhases)
    ,("EOS P2P requests are deferred until lobby membership is confirmed", EosPeerRequestWaitsForMembership)
    ,("EOS receive buffer capacity matches MaxDataSizeBytes and bounds malformed sizes", EosReceiveBufferSizing)
    ,("EOS Product User ID diagnostics are stable truncated hashes", EosProductUserHashesArePrivate)
    ,("EOS client configuration loads file values and environment overrides", EosConfigurationPrecedence)
    ,("EOS client configuration reports missing and malformed sources safely", EosConfigurationValidation)
    ,("Packaged route directory resolves relative to the exported application", PackagedRouteDirectoryResolution)
};

var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS: {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL: {test.Name}: {ex.Message}"); }
}
return failed == 0 ? 0 : 1;

static RideState Tick(RideState state, int power, double grade, double dt = 0.1, double trainerSpeedKph = 30,
    RiderProfile? rider = null, BikeProfile? bike = null, SimulationPhysicsParameters? physics = null)
{
    var input = new SimulationTickInput(
        new TrainerTelemetry(DateTimeOffset.UnixEpoch, power, 80, trainerSpeedKph),
        grade, dt, rider ?? new RiderProfile("Test", 75), bike ?? new BikeProfile("Test", 9));
    return new SimulationEngine(physics).Tick(state, input).NewState;
}

static RideState Run(RideState state, int power, double grade, int ticks, double dt = 0.1)
{
    for (var i = 0; i < ticks; i++) state = Tick(state, power, grade, dt);
    return state;
}

static void HudMetricsUseRideState()
{
	var now = DateTimeOffset.UtcNow;
	var state = RideState.Initial with
	{
		CurrentPowerWatts = 264,
		CurrentCadenceRpm = 85,
		VirtualSpeedKph = 25.5,
		CurrentGradientPercent = 0,
		DistanceMeters = 60
	};
	var rawTelemetry = new TrainerTelemetry(now, 999, 1, 34.5);
	var metrics = RideHudMetrics.From(state, rawTelemetry, now, 12.7);
	if (metrics.PowerWatts != 264 || metrics.CadenceRpm != 85)
		throw new Exception("HUD power and cadence should come from current ride state.");
	Near(25.5, metrics.SimulatedSpeedKph, 0);
	Near(0, metrics.GradientPercent, 0);
	Near(60, metrics.DistanceMeters, 0);
	Near(12.7, metrics.ElapsedSeconds, 0);
	if (metrics.SimulatedSpeedKph == rawTelemetry.SpeedKph)
		throw new Exception("HUD speed must use simulated speed, not raw trainer speed.");
}

static void RemoteRiderInterpolation()
{
    var track = new RemoteRiderInterpolator();
    track.Add(new RemoteRiderSample("r", "Rider", 1990, 18, 70, 150, false, 1, 10));
    track.Add(new RemoteRiderSample("r", "Rider", 2010, 22, 90, 250, false, 2, 10.2));
    if (!track.TrySample(10.3, out var sample)) throw new Exception("Expected interpolated remote sample.");
    Near(2000, sample.DistanceMeters, .01);
    Near(20, sample.SpeedKph, .01);
    if (sample.CadenceRpm != 80 || sample.PowerWatts != 200) throw new Exception("Remote telemetry did not interpolate.");
}

static void RemoteRiderRepositionReset()
{
    var track = new RemoteRiderInterpolator();
    track.Add(new RemoteRiderSample("r", "Rider", 400, 20, 80, 200, false, 1, 10));
    track.Add(new RemoteRiderSample("r", "Rider", 410, 20, 80, 200, false, 2, 10.2));
    track.Reset(new RemoteRiderSample("r", "Rider", 3000, 35, 80, 200, false, 3, 10.3));
    if (!track.TrySample(10.31, out var sample)) throw new Exception("Expected repositioned sample.");
    Near(3000, sample.DistanceMeters, .01);
    Near(35, sample.SpeedKph, .01);
    if (track.Add(new RemoteRiderSample("r", "Rider", 420, 20, 80, 200, false, 2, 10.4)))
        throw new Exception("Older network state was accepted after reposition.");
}

static void RemoteRiderStaleness()
{
    var track = new RemoteRiderInterpolator();
    track.Add(new RemoteRiderSample("r", "Rider", 500, 36, 80, 200, false, 1, 10));
    if (track.TrySample(12.01, out _)) throw new Exception("A stale rider continued to render.");
}

static void RecordingSegmentRebasesAfterTeleport()
{
    var segment = new RecordingProgress();
    segment.Rebase(3000, 125);
    segment.Advance(3000, 4, 125);
    Near(0, segment.DistanceMeters, .001);
    segment.Advance(3012, 4, 125.48);
    Near(12, segment.DistanceMeters, .001);
    Near(125.48, segment.AltitudeMeters, .001);
    Near(.48, segment.AscentMeters, .001);
}

static void CatchUpRoutePosition()
{
    Near(0, MultiplayerRoutePosition.PositionBehind(2), .001);
    Near(5996, MultiplayerRoutePosition.PositionBehind(6000), .001);
    Near(11996, MultiplayerRoutePosition.PositionBehind(12000), .001);
    Near(4, MultiplayerRoutePosition.WrappedSeparation(5998, 2, 6000), .001);
    Near(20, MultiplayerRoutePosition.WrappedSeparation(12010, 11990, 6000), .001);
}

static void SignedRemoteRiderDistance()
{
	Near(4, MultiplayerRoutePosition.SignedWrappedSeparation(2, 5998, 6000), .001);
	Near(-4, MultiplayerRoutePosition.SignedWrappedSeparation(5998, 2, 6000), .001);
	Near(0, MultiplayerRoutePosition.SignedWrappedSeparation(6004, 4, 6000), .001);
	if (MultiplayerRoutePosition.LapDifference(6004, 4, 6000) != 1)
		throw new Exception("Wrapped relative position must retain the remote rider's lap difference.");
}

static void CatchUpMatchesFreshRemoteSpeed()
{
    var speed = CatchUpSpeedResolver.Resolve(35, .1, targetPaused: false);
    Near(35, speed, 0);
    var snapped = RideState.Initial with
    {
        DistanceMeters = 6000,
        VirtualSpeedKph = speed,
        CurrentPowerWatts = 123,
        CurrentCadenceRpm = 71
    };
    var next = Tick(snapped, power: 200, grade: 0, dt: .1);
    if (next.DistanceMeters <= snapped.DistanceMeters || next.VirtualSpeedKph < 30)
        throw new Exception("The first local physics step did not continue from the matched speed.");
    if (next.CurrentPowerWatts != 200 || next.CurrentCadenceRpm != 80)
        throw new Exception("The snap overwrote local power or cadence telemetry.");
}

static void CatchUpSpeedFallbacks()
{
    Near(0, CatchUpSpeedResolver.Resolve(35, 2.1, targetPaused: false), 0);
    Near(0, CatchUpSpeedResolver.Resolve(double.NaN, .1, targetPaused: false), 0);
    Near(0, CatchUpSpeedResolver.Resolve(81, .1, targetPaused: false), 0);
    Near(0, CatchUpSpeedResolver.Resolve(0, .1, targetPaused: false), 0);
    Near(0, CatchUpSpeedResolver.Resolve(35, .1, targetPaused: true), 0);
}

static void TrainerDifficultyScalesSignedGrades()
{
    Near(0, TrainerDifficultyPolicy.ScaleGrade(12, 0), 1e-9);
    Near(3, TrainerDifficultyPolicy.ScaleGrade(12, 25), 1e-9);
    Near(6, TrainerDifficultyPolicy.ScaleGrade(12, 50), 1e-9);
    Near(9, TrainerDifficultyPolicy.ScaleGrade(12, 75), 1e-9);
    Near(12, TrainerDifficultyPolicy.ScaleGrade(12, 100), 1e-9);
    Near(-6, TrainerDifficultyPolicy.ScaleGrade(-12, 50), 1e-9);
    if (FtmsSimulationGrade.Encode(TrainerDifficultyPolicy.ScaleGrade(12, 100), (-6, 6)) != 600 ||
        FtmsSimulationGrade.Encode(TrainerDifficultyPolicy.ScaleGrade(-12, 100), (-6, 6)) != -600)
        throw new Exception("FTMS inclination capability limits were not retained after scaling.");

    // Physics continues to receive the real route grade. TrainerDifficultyPolicy only
    // calculates a separate output command and is not part of SimulationTickInput.
    var physicsAtHalfDifficulty = Tick(RideState.Initial with { VirtualSpeedKph = 25 }, 200, 12);
    var physicsAtFullDifficulty = Tick(RideState.Initial with { VirtualSpeedKph = 25 }, 200, 12);
    Near(physicsAtHalfDifficulty.VirtualSpeedKph, physicsAtFullDifficulty.VirtualSpeedKph, 1e-9);
    if (!(physicsAtHalfDifficulty.VirtualSpeedKph < Tick(RideState.Initial with { VirtualSpeedKph = 25 }, 200, 6).VirtualSpeedKph))
        throw new Exception("The actual 12% route gradient was not retained for cycling physics.");
}

static void CatchUpTrainerGradeTransition()
{
    var transition = new TrainerGradeTransition();
    transition.Start(0, 12);
    Near(4, transition.Advance(1, 12), 1e-9);
    Near(8, transition.Advance(1, 12), 1e-9);
    if (!transition.IsActive) throw new Exception("The catch-up resistance transition ended too early.");
    Near(8 + 1d / 3d, transition.Advance(1, 9), 1e-9);
    Near(9, transition.Advance(2, 9), 1e-9);
    if (transition.IsActive) throw new Exception("The replacement resistance target did not complete.");

    transition.Start(4, 12);
    var pausedValue = transition.CurrentPercent;
    // The ride controller does not call Advance while paused or disconnected.
    Near(4, pausedValue, 1e-9);
    transition.Cancel(4);
    if (transition.IsActive) throw new Exception("Disconnected transition was not cancelled.");
}

static void PausedCatchUpResumeSpeed()
{
    var gate = new RidePauseGate();
    var paused = gate.Pause(RideState.Initial with { DistanceMeters = 4200, VirtualSpeedKph = 20 });
    var beforeResume = gate.Tick(new SimulationEngine(), paused,
        new SimulationTickInput(new TrainerTelemetry(DateTimeOffset.UtcNow, 250, 85, 30), 0, 1,
            new RiderProfile("Test", 75), new BikeProfile("Test", 9)));
    Near(4200, beforeResume.DistanceMeters, 0);
    Near(0, beforeResume.VirtualSpeedKph, 0);
    var resumed = gate.Resume(beforeResume, 35);
    Near(4200, resumed.DistanceMeters, 0);
    Near(35, resumed.VirtualSpeedKph, 0);
    if (gate.IsPaused) throw new Exception("The pause gate remained paused after resuming.");
}

static void HudMetricsMarkStaleSensors()
{
	var now = DateTimeOffset.UtcNow;
	var state = RideState.Initial with { CurrentPowerWatts = 250, CurrentCadenceRpm = 90 };
	var staleTelemetry = new TrainerTelemetry(now - TimeSpan.FromSeconds(6), 250, 90, 30);
	var metrics = RideHudMetrics.From(state, staleTelemetry, now, 8);
	if (metrics.PowerWatts is not null || metrics.CadenceRpm is not null)
		throw new Exception("Stale power and cadence should be displayed as unavailable.");
}

static void ZeroPowerFlatDeceleration()
{
    var moving = RideState.Initial with { VirtualSpeedKph = 30 };
    var next = Tick(moving, 0, 0);
    if (!(next.VirtualSpeedKph < moving.VirtualSpeedKph && next.VirtualSpeedKph > 0))
        throw new Exception("Expected positive flat coasting speed to decrease.");
    var stopped = Run(RideState.Initial with { VirtualSpeedKph = 10 }, 0, 0, 2000);
    Near(0, stopped.VirtualSpeedKph, 0.001);
    if (stopped.DistanceMeters <= 0) throw new Exception("Expected distance while slowing from motion.");
}

static void ZeroPowerDownhillCoasting()
{
    var moving = RideState.Initial with { VirtualSpeedKph = 20 };
    var flat = Run(moving, 0, 0, 100);
    var downhill = Run(moving, 0, -5, 100);
    if (!(downhill.VirtualSpeedKph > moving.VirtualSpeedKph && downhill.VirtualSpeedKph > flat.VirtualSpeedKph))
        throw new Exception($"Expected downhill acceleration, flat={flat.VirtualSpeedKph:F2}, downhill={downhill.VirtualSpeedKph:F2}.");
}

static void UphillSlowerThanFlat()
{
    var start = RideState.Initial with { VirtualSpeedKph = 25 };
    var flat = Run(start, 180, 0, 1000);
    var uphill = Run(start, 180, 6, 1000);
    if (!(uphill.VirtualSpeedKph < flat.VirtualSpeedKph))
        throw new Exception($"Expected uphill speed below flat, flat={flat.VirtualSpeedKph:F2}, uphill={uphill.VirtualSpeedKph:F2}.");
}

static void HigherPowerAcceleratesMore()
{
    var start = RideState.Initial with { VirtualSpeedKph = 20 };
    var low = Tick(start, 100, 2).VirtualSpeedKph;
    var high = Tick(start, 300, 2).VirtualSpeedKph;
    var lowAcceleration = low - start.VirtualSpeedKph;
    var highAcceleration = high - start.VirtualSpeedKph;
    if (!(high > low && highAcceleration > lowAcceleration))
        throw new Exception($"Expected higher power to produce greater acceleration, low={lowAcceleration:F3}, high={highAcceleration:F3}.");
}

static void TenPercentHillStart()
{
    var start = Tick(RideState.Initial, 200, 10, rider: new RiderProfile("Rider", 88),
        bike: new BikeProfile("Bike", 9));
    if (!(start.VirtualSpeedKph > 0 && start.DistanceMeters > 0))
        throw new Exception("200 W did not start the 97 kg rider/bike uphill.");
}

static void EightPercentHillStart()
{
    var start = Tick(RideState.Initial, 150, 8);
    if (!(start.VirtualSpeedKph > 0 && start.DistanceMeters > 0))
        throw new Exception("150 W did not start the rider uphill.");
}

static void ZeroPowerUphillStart()
{
    var stopped = Tick(RideState.Initial, 0, 10);
    Near(0, stopped.VirtualSpeedKph, 0);
    Near(0, stopped.DistanceMeters, 0);
}

static void LaunchReferenceIsConfigurable()
{
    var lowerReference = Tick(RideState.Initial, 100, 0, physics:
        new SimulationPhysicsParameters(PowerForceReferenceSpeedMps: 0.5));
    var higherReference = Tick(RideState.Initial, 100, 0, physics:
        new SimulationPhysicsParameters(PowerForceReferenceSpeedMps: 2.0));
    if (!(lowerReference.VirtualSpeedKph > higherReference.VirtualSpeedKph))
        throw new Exception("Configured launch force reference did not affect low-speed acceleration.");

    var atOne = Tick(RideState.Initial with { VirtualSpeedKph = 3.6 }, 100, 0);
    var atTwo = Tick(RideState.Initial with { VirtualSpeedKph = 7.2 }, 100, 0);
    var accelerationAtOne = atOne.VirtualSpeedKph - 3.6;
    var accelerationAtTwo = atTwo.VirtualSpeedKph - 7.2;
    if (!(accelerationAtOne > accelerationAtTwo))
        throw new Exception("Actual speeds above the reference were not used for drive force.");
}

static void SpeedNeverNegative()
{
    var stopped = Tick(RideState.Initial, 0, 15, dt: 10);
    var descending = Tick(RideState.Initial with { VirtualSpeedKph = 0.01 }, 0, 90, dt: 100);
    if (stopped.VirtualSpeedKph < 0 || descending.VirtualSpeedKph < 0)
        throw new Exception("Speed became negative.");
    Near(0, stopped.VirtualSpeedKph, 0.001);
    Near(0, stopped.DistanceMeters, 0.001);
}

static void TrainerSpeedIsIgnored()
{
    var start = RideState.Initial with { VirtualSpeedKph = 12 };
    var slowTrainer = Tick(start, 100, 0, trainerSpeedKph: 2);
    var fastTrainer = Tick(start, 100, 0, trainerSpeedKph: 80);
    Near(slowTrainer.VirtualSpeedKph, fastTrainer.VirtualSpeedKph, 1e-12);
    Near(slowTrainer.DistanceMeters, fastTrainer.DistanceMeters, 1e-12);
}

static void ProfilesAffectPhysics()
{
    var start = RideState.Initial with { VirtualSpeedKph = 15 };
    var light = Tick(start, 200, 0, rider: new RiderProfile("Light", 50));
    var heavy = Tick(start, 200, 0, rider: new RiderProfile("Heavy", 100));
    var lowDragBike = Tick(start, 200, 0, bike: new BikeProfile("Low drag", 9, 0.003, 0.2));
    if (!(light.VirtualSpeedKph > heavy.VirtualSpeedKph && lowDragBike.VirtualSpeedKph > heavy.VirtualSpeedKph))
        throw new Exception("Rider mass / bike road-load values did not affect acceleration as expected.");
}

static void RidePauseSemantics()
{
    var engine = new SimulationEngine();
    var gate = new RidePauseGate();
    var input = new SimulationTickInput(new TrainerTelemetry(DateTimeOffset.UnixEpoch, 250, 85, 30),
        0, 0.1, new RiderProfile("Test", 75), new BikeProfile("Test", 9));
    var moving = RideState.Initial with { DistanceMeters = 100, ElapsedSeconds = 20, VirtualSpeedKph = 24 };
    moving = gate.Tick(engine, moving, input);
    var parked = gate.Pause(moving);
    Near(0, parked.VirtualSpeedKph, 0);
    for (var i = 0; i < 50; i++)
    {
        parked = gate.Tick(engine, parked, input);
        Near(moving.DistanceMeters, parked.DistanceMeters, 0);
        Near(moving.ElapsedSeconds, parked.ElapsedSeconds, 0);
        Near(0, parked.VirtualSpeedKph, 0);
    }
    var resumePosition = parked.DistanceMeters;
    gate.Resume();
    Near(resumePosition, parked.DistanceMeters, 0);
    var resumed = gate.Tick(engine, parked, input);
    if (resumed.DistanceMeters <= resumePosition || resumed.VirtualSpeedKph <= 0)
        throw new Exception("Motion did not resume from rest after power was applied.");
    if (resumed.DistanceMeters - resumePosition > 0.1)
        throw new Exception("Resume introduced a distance jump.");
}

static void RidePauseToggle()
{
    var engine = new SimulationEngine();
    var gate = new RidePauseGate(); // No recorder is present: ride pause is independent.
    var input = new SimulationTickInput(new TrainerTelemetry(DateTimeOffset.UnixEpoch, 200, 80, 20),
        0, 0.1, new RiderProfile("Test", 75), new BikeProfile("Test", 9));
    var state = engine.Tick(RideState.Initial, input).NewState;
    var distance = state.DistanceMeters;
    for (var i = 0; i < 3; i++)
    {
        state = gate.Toggle(state);
        if (!gate.IsPaused) throw new Exception("Pause action did not enter PAUSED state.");
        Near(0, state.VirtualSpeedKph, 0);
        var frozenDistance = state.DistanceMeters;
        var frozenElapsed = state.ElapsedSeconds;
        for (var j = 0; j < 10; j++) state = gate.Tick(engine, state, input);
        Near(frozenDistance, state.DistanceMeters, 0);
        Near(frozenElapsed, state.ElapsedSeconds, 0);

        state = gate.Toggle(state);
        if (gate.IsPaused) throw new Exception("Second Pause action did not resume the ride.");
        Near(0, state.VirtualSpeedKph, 0);
        Near(frozenDistance, state.DistanceMeters, 0);
        state = gate.Tick(engine, state, input);
        if (state.DistanceMeters <= frozenDistance || state.VirtualSpeedKph <= 0)
            throw new Exception("Ride did not restart from zero speed after toggle resume.");
    }
    if (state.DistanceMeters <= distance) throw new Exception("Repeated toggles lost resumed movement.");
}

static void DebugModeOff()
{
    var diagnostics = new DebugRideDiagnostics();
    diagnostics.Begin(false);
    if (diagnostics.SnapshotDue(10) || diagnostics.NextMarkerNumber() != 0)
        throw new Exception("Disabled diagnostics emitted periodic or marker data.");
}

static void DebugSnapshotFields()
{
    var diagnostics = new DebugRideDiagnostics();
    diagnostics.Begin(true);
    if (diagnostics.SnapshotDue(0.24) || !diagnostics.SnapshotDue(0.26))
        throw new Exception("Expected a diagnostic snapshot at the 0.5 second interval.");
    var snapshot = diagnostics.CreateSnapshot(new RideDebugSnapshot
    {
        totalRideDistanceMeters = 5851,
        routeLengthMeters = 5850,
        virtualSpeedKph = 27.4,
        ridePaused = true,
        rideState = "paused",
        visualSampleBefore = 585,
        visualSampleAfter = 586,
        visualInterpolation = 0.42
    }, DateTimeOffset.UnixEpoch);
    if (snapshot.totalRideDistanceMeters != 5851 || snapshot.virtualSpeedKph != 27.4
        || !snapshot.ridePaused || snapshot.wrappedRouteDistanceMeters != 1 || snapshot.lapNumber != 2
        || snapshot.debugSessionId != diagnostics.SessionId || snapshot.timestamp != DateTimeOffset.UnixEpoch)
        throw new Exception("Snapshot omitted ride state or wrapped route fields.");
}

static void DebugMarkerNumbers()
{
    var diagnostics = new DebugRideDiagnostics();
    diagnostics.Begin(true);
    if (diagnostics.NextMarkerNumber() != 1 || diagnostics.NextMarkerNumber() != 2)
        throw new Exception("Debug markers did not increment monotonically.");
}

static void ScreenshotNames()
{
    var timestamp = new DateTimeOffset(2026, 10, 8, 14, 23, 15, TimeSpan.FromHours(13));
    var calls = 0;
    var stem = ScreenshotCaptureFileStore.CreateUniqueStem(timestamp, ScreenshotCaptureMode.DebugMarker, 3,
        path => ++calls <= 2); // First stem's PNG and JSON exist.
    if (!stem.Contains("20261008-142315-marker-003-002", StringComparison.Ordinal))
        throw new Exception($"Unexpected collision-safe marker stem: {stem}");
    if (Path.GetFileNameWithoutExtension(stem + ".png") != Path.GetFileNameWithoutExtension(stem + ".json")
        || !stem.Contains("marker-003", StringComparison.Ordinal))
        throw new Exception("Marker PNG/JSON names did not remain associated.");
    if (ScreenshotCaptureFileStore.CreateUniqueStem(timestamp, ScreenshotCaptureMode.Clean, null, _ => false)
        != "wodra-20261008-142315-clean") throw new Exception("Clean filename format changed unexpectedly.");
    if (ScreenshotCaptureFileStore.CreateUniqueStem(timestamp, ScreenshotCaptureMode.Hud, null, _ => false)
        != "wodra-20261008-142315-hud"
        || ScreenshotCaptureFileStore.CreateUniqueStem(timestamp, ScreenshotCaptureMode.DebugMarker, 3, _ => false)
        != "wodra-20261008-142315-marker-003")
        throw new Exception("HUD or marker screenshot filename did not use Wodra naming.");
}

static void ScreenshotMetadataFields()
{
    var snapshot = new RideDebugSnapshot
    {
        timestamp = DateTimeOffset.UnixEpoch, marker = 3, routeId = "test-route", routeName = "Test Route",
        totalRideDistanceMeters = 1200, wrappedRouteDistanceMeters = 200, lapNumber = 2,
        virtualSpeedKph = 31.2, rawTrainerSpeedKph = 30.8, rawTrainerPowerWatts = 240,
        rawTrainerCadenceRpm = 88, routeGradientPercent = 2.5, routeElevationMeters = 43,
        visualSampleBefore = 12, visualSampleAfter = 13, visualInterpolation = .4,
        riderWorldPosition = new DebugVector3(1, 2, 3), cameraWorldPosition = new DebugVector3(4, 5, 6),
        ridePaused = false, fitRecordingState = "recording", nearestLandmarkName = "Bridge",
        nearestLandmarkDistanceMeters = 24, nearbyLandmarkCount = 2, buildingsLoaded = 7,
        visibleBuildingChunks = 3, vegetationAreas = 4, treesLoaded = 9, visibleVegetationChunks = 2,
        terrainNearChunks = 8, terrainFarChunks = 6, terrainVisibleChunks = 10
    };
    using var json = JsonDocument.Parse(ScreenshotCaptureFileStore.SerializeSidecar(snapshot));
    var root = json.RootElement;
    if (root.GetProperty("marker").GetInt32() != 3 || root.GetProperty("routeId").GetString() != "test-route"
        || root.GetProperty("wrappedRouteDistanceMeters").GetDouble() != 200
        || root.GetProperty("powerWatts").GetInt32() != 240 || root.GetProperty("nearTerrainChunks").GetInt32() != 8)
        throw new Exception("Screenshot sidecar omitted requested ride metadata.");
    if (root.EnumerateObject().Count() > 40) throw new Exception("Screenshot sidecar is no longer compact.");
}

static void ScreenshotModeSelection()
{
    if (ScreenshotCapturePolicy.ForF12(false) != ScreenshotCaptureMode.Clean
        || ScreenshotCapturePolicy.ForF12(true) != ScreenshotCaptureMode.Hud
        || ScreenshotCapturePolicy.ForDebugMarker() != ScreenshotCaptureMode.DebugMarker)
        throw new Exception("Screenshot capture modes do not match their requested controls.");
}

static void ScreenshotDirectoryFallback()
{
    var root = Path.Combine(Path.GetTempPath(), "wodra-screenshot-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var pictures = Path.Combine(root, "Pictures");
        var preferred = ScreenshotCaptureFileStore.CreateScreenshotDirectory(pictures, Path.Combine(root, "unused"));
        if (preferred != Path.Combine(pictures, "Wodra") || !Directory.Exists(preferred))
            throw new Exception("Screenshot store did not create Pictures/Wodra.");
        var blocker = Path.Combine(root, "pictures-file");
        File.WriteAllText(blocker, "not a directory");
        var fallback = Path.Combine(root, "godot-user-data");
        var resolved = ScreenshotCaptureFileStore.CreateScreenshotDirectory(blocker, fallback);
        if (resolved != Path.Combine(fallback, "screenshots") || !Directory.Exists(resolved))
            throw new Exception("Screenshot store did not create its user-data fallback.");
        try { ScreenshotCaptureFileStore.CreateScreenshotDirectory(blocker, blocker); }
        catch (IOException) { return; }
        throw new Exception("Screenshot directory failure was not reported when both locations failed.");
    }
    finally { Directory.Delete(root, recursive: true); }
}

static void ScreenshotFailureKeepsMarker()
{
    var diagnostics = new DebugRideDiagnostics(); diagnostics.Begin(true);
    var marker = diagnostics.NextMarkerNumber();
    var loggedMarker = marker;
    var failureReported = false;
    var saved = ScreenshotCaptureFailurePolicy.TryCapture(() => throw new IOException("disk full"), _ => failureReported = true);
    if (saved || !failureReported || loggedMarker != 1 || diagnostics.NextMarkerNumber() != 2)
        throw new Exception("Screenshot failure suppressed or altered debug marker numbering.");
}

static void DebugDistanceJump()
{
    var diagnostics = new DebugRideDiagnostics();
    diagnostics.Begin(true);
    var current = RideState.Initial with { DistanceMeters = 100, ElapsedSeconds = 30, VirtualSpeedKph = 25, ElevationGainMeters = 42 };
    if (!diagnostics.TryApplyDistanceJump(current, 2130, out var jumped))
        throw new Exception("Debug jump was rejected while recording; recording must be finalized by the session first.");
    Near(2130, jumped.DistanceMeters, 0);
    Near(0, jumped.VirtualSpeedKph, 0);
    Near(current.ElapsedSeconds, jumped.ElapsedSeconds, 0);
    Near(current.ElevationGainMeters, jumped.ElevationGainMeters, 0);
    if (!diagnostics.TryApplyDistanceJump(jumped, 12_000, out var laterLap))
        throw new Exception("Jump to an absolute distance on a later lap was rejected.");
    Near(12_000, laterLap.DistanceMeters, 0);
    if (diagnostics.TryApplyDistanceJump(current, double.NaN, out _) ||
        diagnostics.TryApplyDistanceJump(current, -1, out _))
        throw new Exception("Invalid route jump distance was accepted.");
    diagnostics.Begin(false);
    if (diagnostics.TryApplyDistanceJump(current, 5830, out _))
        throw new Exception("Jump was allowed outside Debug mode.");
}

static void BuildingFallbackVariation()
{
    var first = BuildingVisualStyle.ResolveFallbackVisualHeight(901, "generic", 72, 8);
    Near(first, BuildingVisualStyle.ResolveFallbackVisualHeight(901, "generic", 72, 8), 0);
    if (first is < 3.3f or > 4.8f) throw new Exception($"Fallback low-rise height was outside its visual band: {first} m.");
    var variants = Enumerable.Range(1, 40)
        .Select(id => BuildingVisualStyle.ResolveFallbackVisualHeight(id, "generic", 72, 8)).Distinct().Count();
    if (variants < 10) throw new Exception("Fallback buildings did not get varied deterministic heights.");
    Near(3, BuildingVisualStyle.ResolveFallbackVisualHeight(901, "industrial/service", 72, 3), 0);

    var roofs = Enumerable.Range(1, 80)
        .Select(id => BuildingVisualStyle.ResolveRoofShape(id, null, "generic", "Fallback", 90, 8)).ToArray();
    if (!roofs.Contains("flat") || !roofs.Contains("gabled") || !roofs.Contains("hipped"))
        throw new Exception("Unknown low-rise roofs did not receive the expected restrained style mix.");
    if (BuildingVisualStyle.ResolveRoofShape(9, "gabled", "generic", "Fallback", 90, 8) != "gabled")
        throw new Exception("Mapped roof metadata was not preserved.");
    var brick = BuildingVisualStyle.FacadeColor(0, null, "brick");
    if (brick != new BuildingRgb(.57f, .34f, .27f)) throw new Exception("Mapped brick facade material was not used.");
    var explicitColour = BuildingVisualStyle.FacadeColor(0, "#abc", "brick");
    if (explicitColour == brick) throw new Exception("Explicit mapped building colour did not take priority over material.");
}

static void BuildingFacadeVariation()
{
    var ring = new[]
    {
        new System.Numerics.Vector3(-12, 0, -6), new System.Numerics.Vector3(12, 0, -6),
        new System.Numerics.Vector3(12, 0, 6), new System.Numerics.Vector3(-12, 0, 6),
        new System.Numerics.Vector3(-12, 0, -6)
    };
    var firstPattern = BuildingVisualStyle.FacadePattern(431);
    var first = BuildingMeshGeometry.BuildStyled(ring, 9, "flat", 0, 3, true, facadePattern: firstPattern);
    var repeat = BuildingMeshGeometry.BuildStyled(ring, 9, "flat", 0, 3, true, facadePattern: firstPattern);
    var other = BuildingMeshGeometry.BuildStyled(ring, 9, "flat", 0, 3, true,
        facadePattern: BuildingVisualStyle.FacadePattern(877));
    if (!first.Windows.SequenceEqual(repeat.Windows)) throw new Exception("Facade pattern was not repeatable.");
    if (first.Windows.SequenceEqual(other.Windows)) throw new Exception("Distinct buildings got identical window rhythms.");
}

static void Near(double expected, double actual, double tolerance)
{
    if (Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected}, got {actual}.");
}

static void StartupOnlineStatusIsCompact()
{
    if (!OnlineStatusText.UsesEos("EOS") || !OnlineStatusText.UsesEos("EpicOnlineServices") || OnlineStatusText.UsesEos("WebSocket"))
        throw new Exception("The configured EOS provider names should be recognized consistently.");
    if (OnlineStatusText.ForStartup(false, false, null, false, true) != "Online Riding: Off")
        throw new Exception("New offline users should see the compact off state.");
    if (OnlineStatusText.ForStartup(true, false, false, false, true) != "EOS not configured")
        throw new Exception("Missing EOS settings should have a concise unavailable state.");
    if (OnlineStatusText.ForStartup(true, false, false, true, true, eosConfigurationInvalid: true) != "EOS configuration invalid")
        throw new Exception("Invalid EOS settings should have a concise configuration error state.");
    if (OnlineStatusText.ForStartup(true, false, false, true, true) != "Online unavailable")
        throw new Exception("A temporary EOS outage should not expose provider diagnostics.");
    if (OnlineStatusText.ForStartup(true, false, false, true, true, eosRuntimeUnavailable: true) != "EOS runtime unavailable")
        throw new Exception("A failed native EOS load should be named clearly in the UI.");
    if (OnlineStatusText.ForStartup(true, false, false, true, false) != "Online unavailable")
        throw new Exception("A WebSocket outage should stay concise.");
    if (OnlineStatusText.ForStartup(true, true, true, true, true) != "Online")
        throw new Exception("Connected state should be clear and concise.");
}

static void EosLobbyStatusPhases()
{
    if (EosLobbyStatusText.ForMembership(1, 0, 0, false) != "EOS lobby joined · 1 rider")
        throw new Exception("One member should be reported as a joined singleton lobby.");
    if (EosLobbyStatusText.ForMembership(2, 0, 0, false) != "EOS lobby joined · 2 riders · peers discovered")
        throw new Exception("Lobby membership should be distinguished from P2P negotiation.");
    if (EosLobbyStatusText.ForMembership(2, 1, 0, false) != "EOS lobby joined · 2 riders · waiting for rider state")
        throw new Exception("An established transport without state must not be reported as gameplay-connected.");
    if (EosLobbyStatusText.ForMembership(2, 1, 1, false) != "EOS lobby joined · 2 riders · rider-state exchange active")
        throw new Exception("Accepted live state should be reported as an active exchange.");
    if (EosLobbyStatusText.ForMembership(2, 0, 0, true) != "EOS lobby joined · 2 riders · P2P failed")
        throw new Exception("A P2P failure should remain distinct from authentication or lobby join.");
}

static void EosPeerRequestWaitsForMembership()
{
	var gate = new EosPeerMembershipGate();
	if (gate.OnIncomingRequest("peer-1"))
		throw new Exception("An unknown peer must not be accepted before the lobby confirms membership.");
	if (!gate.AddMember("peer-1"))
		throw new Exception("The deferred request should become eligible when membership arrives.");
	if (!gate.OnIncomingRequest("peer-1"))
		throw new Exception("A confirmed lobby member's next P2P request should be accepted immediately.");
	gate.RemoveMember("peer-1");
	if (gate.OnIncomingRequest("peer-1"))
		throw new Exception("A peer who left the lobby must no longer be accepted.");
}

static void EosReceiveBufferSizing()
{
	const int maximum = 8192;
	var small = EosReceiveBufferPlan.ForPacket(236, maximum);
	if (small.BufferLength != 236 || small.MaxDataSizeBytes != 236 || small.Oversized)
		throw new Exception("Small packets need a matching allocated buffer and EOS receive capacity.");
	var empty = EosReceiveBufferPlan.ForPacket(0, maximum);
	if (empty.BufferLength != maximum || empty.MaxDataSizeBytes != maximum || empty.Oversized)
		throw new Exception("A zero reported size must still use a bounded, explicit receive capacity and be rejected after receive.");
	var large = EosReceiveBufferPlan.ForPacket(maximum + 1u, maximum);
	if (large.BufferLength != maximum || large.MaxDataSizeBytes != maximum || !large.Oversized)
		throw new Exception("Oversized packets must be drained only into a bounded buffer, then discarded.");
	var next = EosReceiveBufferPlan.ForPacket(64, maximum);
	if (next.BufferLength != 64 || next.MaxDataSizeBytes != 64 || next.Oversized)
		throw new Exception("Buffer capacity must be recalculated independently for successive packets.");
}

static void EosProductUserHashesArePrivate()
{
    const string productUserId = "test-product-user-id-value";
    var first = EosIdentityDiagnostics.HashProductUserId(productUserId);
    var second = EosIdentityDiagnostics.HashProductUserId(productUserId);
    if (first != second || first.Length != 16 || first == productUserId)
        throw new Exception("Product User ID diagnostics must be stable, truncated, and must not expose the source ID.");
    if (first == EosIdentityDiagnostics.HashProductUserId("another-product-user-id"))
        throw new Exception("Distinct Product User IDs should produce distinct diagnostic hashes.");
    if (EosIdentityDiagnostics.HashProductUserId(null) != "missing")
        throw new Exception("Missing Product User IDs should be represented safely.");
}

static void EosConfigurationPrecedence()
{
    var root = Path.Combine(Path.GetTempPath(), "wodra-eos-config-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var userFile = Path.Combine(root, "user.json");
        var packagedFile = Path.Combine(root, "packaged.json");
        File.WriteAllText(userFile, """{"ProductId":"file-product","SandboxId":"file-sandbox","DeploymentId":"file-deployment","ClientId":"file-client","ClientSecret":"file-secret"}""");
        File.WriteAllText(packagedFile, """{"ProductId":"package-product","SandboxId":"package-sandbox","DeploymentId":"package-deployment","ClientId":"package-client","ClientSecret":"package-secret"}""");
        var fileLoaded = EosClientConfiguration.LoadFromSources(new Dictionary<string, string?>(), new[] { userFile, packagedFile });
        if (!fileLoaded.IsConfigured || fileLoaded.SourcePath != Path.GetFullPath(userFile) || fileLoaded.ProductId != "file-product")
            throw new Exception("The first (per-user) JSON candidate should load before the packaged file.");

        var overridden = EosClientConfiguration.LoadFromSources(new Dictionary<string, string?>
        {
            ["WODRA_EOS_PRODUCT_ID"] = "environment-product",
            ["WODRA_EOS_CLIENT_SECRET"] = "environment-secret"
        }, new[] { userFile, packagedFile });
        if (!overridden.IsConfigured || overridden.ProductId != "environment-product" || overridden.ClientSecret != "environment-secret" || overridden.SandboxId != "file-sandbox")
            throw new Exception("Non-empty environment values should override matching file fields only.");
    }
    finally { Directory.Delete(root, recursive: true); }
}

static void EosConfigurationValidation()
{
    var root = Path.Combine(Path.GetTempPath(), "wodra-eos-config-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var malformed = Path.Combine(root, "eos-client.json");
        const string secretSentinel = "NEVER-LOG-THIS-SECRET";
        File.WriteAllText(malformed, secretSentinel);
        var invalid = EosClientConfiguration.LoadFromSources(new Dictionary<string, string?>(), new[] { malformed });
        if (!invalid.IsInvalid || invalid.IsConfigured || invalid.LoadError!.Contains(secretSentinel, StringComparison.Ordinal))
            throw new Exception("Malformed configuration should yield a generic diagnostic without echoing file contents.");

        var missing = EosClientConfiguration.LoadFromSources(new Dictionary<string, string?>(), Array.Empty<string>());
        if (missing.IsInvalid || missing.IsConfigured || missing.MissingVariables.Count != 5)
            throw new Exception("Missing configuration should be distinguishable from malformed configuration and identify required fields.");
        try { missing.Validate(); throw new Exception("Missing configuration should fail validation."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("WODRA_EOS_CLIENT_SECRET", StringComparison.Ordinal)) { }
    }
    finally { Directory.Delete(root, recursive: true); }
}

static void PackagedRouteDirectoryResolution()
{
    var exportRoot = Path.Combine(Path.GetTempPath(), "WodraExport");
    var resolved = RoutePackageFiles.ResolvePackagedPackagesDirectory(Path.Combine(exportRoot, "wodra.exe"),
        Path.Combine(exportRoot, "managed"));
    if (!string.Equals(resolved, Path.Combine(exportRoot, "Routes"), StringComparison.OrdinalIgnoreCase))
        throw new Exception("Exported route data should resolve from the executable directory, regardless of current working directory.");

    var fallback = RoutePackageFiles.ResolvePackagedPackagesDirectory(null, Path.Combine(exportRoot, "managed"));
    if (!string.Equals(fallback, Path.Combine(exportRoot, "managed", "Routes"), StringComparison.OrdinalIgnoreCase))
        throw new Exception("The application base directory should be used when no executable path is available.");
}
