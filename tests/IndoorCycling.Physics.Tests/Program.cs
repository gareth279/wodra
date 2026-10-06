using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;

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
