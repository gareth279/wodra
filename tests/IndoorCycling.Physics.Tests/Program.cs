using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
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
    ("Mass and bike resistance profile affect acceleration", ProfilesAffectPhysics),
    ("Ride pause freezes progress and resumes without a distance jump", RidePauseSemantics),
    ("Repeated ride pause action toggles explicit ride state", RidePauseToggle),
    ("Debug mode off emits no periodic snapshots", DebugModeOff),
    ("Debug snapshot contains ride state and calculated lap position", DebugSnapshotFields),
    ("Debug marker numbering increments", DebugMarkerNumbers),
    ("Screenshot filename and marker sidecar share collision-safe names", ScreenshotNames),
    ("Screenshot sidecar contains compact ride metadata", ScreenshotMetadataFields),
    ("F12 selects clean or visible HUD capture", ScreenshotModeSelection),
    ("Screenshot directory falls back when Pictures is unavailable", ScreenshotDirectoryFallback),
    ("Screenshot failure does not interrupt debug marker creation", ScreenshotFailureKeepsMarker),
    ("Debug distance jump resets speed and blocks active FIT recording", DebugDistanceJump)
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
    var current = RideState.Initial with { DistanceMeters = 100, ElapsedSeconds = 30, VirtualSpeedKph = 25 };
    if (diagnostics.TryApplyDistanceJump(current, 5830, true, out _))
        throw new Exception("Jump was allowed during active FIT recording.");
    if (!diagnostics.TryApplyDistanceJump(current, 5830, false, out var jumped))
        throw new Exception("Debug jump was rejected without active FIT recording.");
    Near(5830, jumped.DistanceMeters, 0);
    Near(0, jumped.VirtualSpeedKph, 0);
    Near(current.ElapsedSeconds, jumped.ElapsedSeconds, 0);
    diagnostics.Begin(false);
    if (diagnostics.TryApplyDistanceJump(current, 5830, false, out _))
        throw new Exception("Jump was allowed outside Debug mode.");
}

static void Near(double expected, double actual, double tolerance)
{
    if (Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected}, got {actual}.");
}
