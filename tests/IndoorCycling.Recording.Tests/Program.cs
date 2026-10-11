using Dynastream.Fit;
using System.Buffers.Binary;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Models;

var tests = new (string Name, Action Run)[]
{
    ("First sample establishes route altitude without ascent", () =>
    {
        var progress = new RecordingProgress();
        Near(0, progress.Advance(1000, 5, 7));
        Near(7, progress.AltitudeMeters);
        Near(0, progress.DistanceMeters);
        Near(0, progress.AscentMeters);
        Near(0, progress.DescentMeters);
    }),
    ("Grade integration uses successive distance deltas", () =>
    {
        var progress = new RecordingProgress();
        progress.Rebase(500, null);
        progress.Advance(600, 5, null);
        progress.Advance(700, 5, null);
        progress.Advance(750, -4, null);
        Near(250, progress.DistanceMeters);
        Near(8, progress.AltitudeMeters);
        Near(10, progress.AscentMeters);
        Near(2, progress.DescentMeters);
    }),
    ("Pause rebase excludes distance and elevation during pause", () =>
    {
        var progress = new RecordingProgress();
        progress.Rebase(100, 5);
        progress.Advance(150, 0, 8);
        progress.Rebase(1000, 50);
        progress.Advance(1020, 0, 49);
        Near(70, progress.DistanceMeters);
        Near(3, progress.AscentMeters);
        Near(1, progress.DescentMeters);
    }),
    ("Repeated samples and decreasing raw distance add no distance", () =>
    {
        var progress = new RecordingProgress();
        progress.Rebase(100, null);
        progress.Advance(100, 5, null);
        progress.Advance(0, 5, null);
        progress.Advance(10, 5, null);
        Near(10, progress.DistanceMeters);
        Near(0.5, progress.AscentMeters);
    }),
    ("Late route altitude establishes a baseline without a jump", () =>
    {
        var progress = new RecordingProgress();
        progress.Rebase(0, null);
        progress.Advance(100, 5, null);
        progress.Advance(200, 0, 1000);
        Near(5, progress.AscentMeters);
        Near(1000, progress.AltitudeMeters);
    }),
    ("FTMS simulation grade encoding honors advertised range and protocol bounds", () =>
    {
        if (FtmsSimulationGrade.Encode(30, (-10, 20)) != 2000
            || FtmsSimulationGrade.Encode(-12, (-10, 20)) != -1000
            || FtmsSimulationGrade.Encode(400) != short.MaxValue
            || FtmsSimulationGrade.Encode(-400) != short.MinValue
            || FtmsSimulationGrade.Encode(double.NaN) != 0)
            throw new Exception("FTMS grade was not safely encoded or clamped.");
    }),
    ("FTMS Supported Inclination Range decodes signed tenths of a percent", () =>
    {
        var value = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(value.AsSpan(0, 2), -100);
        BinaryPrimitives.WriteInt16LittleEndian(value.AsSpan(2, 2), 250);
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(4, 2), 5);
        if (!FtmsSimulationGrade.TryDecodeSupportedInclinationRange(value, out var range)
            || Math.Abs(range.Minimum - -10) > 0.001 || Math.Abs(range.Maximum - 25) > 0.001
            || FtmsSimulationGrade.Encode(30, range) != 2500
            || FtmsSimulationGrade.TryDecodeSupportedInclinationRange(value.AsSpan(0, 4), out _))
            throw new Exception("FTMS inclination limits were not decoded or applied correctly.");
    }),
    ("FIT round trip retains active distance and elevation totals", () => FitRoundTrip(false)),
    ("Stopping a paused FIT finalizes it without paused travel", () => FitRoundTrip(true)),
    ("Ride pause pauses and resumes the same active FIT recording", RidePauseResumesSameRecording),
    ("Starting recording while paused does not resume ride or FIT timer", StartRecordingWhileRidePaused),
    ("Stopping FIT recording leaves the ride running", StopRecordingLeavesRideRunning),
    ("Finalized FIT stays valid and ignores post-jump ride samples", FinalizedFitExcludesLaterDebugJump),
    ("Consecutive rides create separate FIT files", ConsecutiveRidesCreateSeparateFiles),
    ("Empty recording finalizes as a valid FIT file", EmptyRecording)
};

var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS: {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL: {test.Name}: {ex}"); }
}
return failed == 0 ? 0 : 1;

static void Near(double expected, double actual)
{
    if (Math.Abs(expected - actual) > 0.01)
        throw new Exception($"Expected {expected}, got {actual}.");
}

static RideState State(double distance) => RideState.Initial with { DistanceMeters = distance, VirtualSpeedKph = 30 };
static RouteSample Route(double elevation) => new(0, 0, -45.88, 170.50, elevation, "Test");
static TrainerTelemetry Telemetry() => new(DateTimeOffset.UtcNow, 180, 85, 30);

static void FitRoundTrip(bool stopPaused)
{
    InTemporaryFolder(() =>
    {
        var recorder = new RideFitRecorder();
        recorder.StartOrResume(100, 5);
        recorder.RecordSample(State(100), Telemetry(), 0, Route(5));
        recorder.RecordSample(State(150), Telemetry(), 0, Route(8));
        recorder.Pause();
        recorder.RecordSample(State(900), Telemetry(), 0, Route(99));
        if (!stopPaused)
        {
            recorder.StartOrResume(1000, 50);
            recorder.RecordSample(State(1020), Telemetry(), 0, Route(49));
        }
        var path = recorder.Stop()!;
        if (path != recorder.FilePath || recorder.Stop() != path || recorder.IsRecording || recorder.IsPaused)
            throw new Exception("Finalization did not retain its path/state.");
        var frames = new FitRideLoader().Load(path);
        Near(stopPaused ? 2 : 3, frames.Count);
        Near(stopPaused ? 50 : 70, frames[^1].DistanceMeters!.Value);
        Near(5, frames[0].ElevationMeters!.Value);
        using var stream = System.IO.File.OpenRead(path);
        var decode = new Decode();
        var broadcaster = new MesgBroadcaster();
        SessionMesg? session = null;
        broadcaster.SessionMesgEvent += (_, e) => session = new SessionMesg(e.mesg);
        decode.MesgEvent += broadcaster.OnMesg;
        decode.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
        decode.Read(stream);
        if (session is null) throw new Exception("Missing finalized session summary.");
        Near(stopPaused ? 50 : 70, session.GetTotalDistance()!.Value);
        Near(3, session.GetTotalAscent()!.Value);
        Near(stopPaused ? 0 : 1, session.GetTotalDescent()!.Value);
    });
}

static void EmptyRecording() => InTemporaryFolder(() =>
{
    var recorder = new RideFitRecorder();
    recorder.StartOrResume(0);
    var path = recorder.Stop()!;
    Near(0, new FitRideLoader().Load(path).Count);
});

static void RidePauseResumesSameRecording() => InTemporaryFolder(() =>
{
    var gate = new RidePauseGate();
    var recorder = new RideFitRecorder();
    var state = State(50);
    var originalPath = recorder.StartOrResume(state.DistanceMeters, 5);
    recorder.RecordSample(state, Telemetry(), 0, Route(5));

    state = gate.Toggle(state);
    recorder.Pause();
    var frozenDistance = state.DistanceMeters;
    for (var i = 0; i < 10; i++)
        state = gate.Tick(new SimulationEngine(), state,
            new SimulationTickInput(Telemetry(), 0, 1, new RiderProfile("Test", 75), new BikeProfile("Test", 9)));
    recorder.RecordSample(State(900), Telemetry(), 0, Route(99));
    Near(frozenDistance, state.DistanceMeters);
    if (!gate.IsPaused || !recorder.IsPaused) throw new Exception("Ride and FIT were not paused together.");

    state = gate.Toggle(state);
    var resumedPath = recorder.StartOrResume(state.DistanceMeters, 5);
    if (originalPath != resumedPath || !recorder.IsRecording || gate.IsPaused)
        throw new Exception("Resume did not continue the existing recording and ride.");
    recorder.RecordSample(State(state.DistanceMeters + 10), Telemetry(), 0, Route(5));
    var finalPath = recorder.Stop()!;
    if (finalPath != originalPath) throw new Exception("Resume created a different FIT file.");
    Near(10, new FitRideLoader().Load(finalPath)[^1].DistanceMeters!.Value);
});

static void StartRecordingWhileRidePaused() => InTemporaryFolder(() =>
{
    var gate = new RidePauseGate();
    var state = gate.Pause(State(100));
    var recorder = new RideFitRecorder();
    recorder.StartOrResume(state.DistanceMeters, 5);
    recorder.Pause();
    if (!gate.IsPaused || !recorder.IsPaused || recorder.IsRecording)
        throw new Exception("Starting FIT while paused resumed the ride or recording timer.");
    recorder.Stop();
});

static void StopRecordingLeavesRideRunning() => InTemporaryFolder(() =>
{
    var gate = new RidePauseGate();
    var recorder = new RideFitRecorder();
    recorder.StartOrResume(0);
    var state = gate.Toggle(State(0)); // Paused.
    state = gate.Toggle(state); // Running again.
    recorder.Stop();
    if (gate.IsPaused) throw new Exception("Stopping recording paused the ride.");
});

static void FinalizedFitExcludesLaterDebugJump() => InTemporaryFolder(() =>
{
    var recorder = new RideFitRecorder();
    recorder.StartOrResume(100, 12);
    recorder.RecordSample(State(100), Telemetry(), 0, Route(12));
    recorder.RecordSample(State(200), Telemetry(), 1.5, Route(13.5));
    var path = recorder.Stop()!;

    // A route jump happens only after the active FIT has been closed.
    recorder.RecordSample(State(2130), Telemetry(), 4, Route(80));
    if (recorder.Stop() != path || recorder.IsRecording)
        throw new Exception("The finalized recording restarted or changed after the debug jump.");
    var frames = new FitRideLoader().Load(path);
    Near(2, frames.Count);
    Near(100, frames[^1].DistanceMeters!.Value);
    if (frames[^1].ElevationMeters!.Value > 20)
        throw new Exception("Post-jump route elevation leaked into the finalized FIT file.");
});

static void ConsecutiveRidesCreateSeparateFiles() => InTemporaryFolder(() =>
{
    var first = new RideFitRecorder();
    first.StartOrResume(0);
    var firstPath = first.Stop()!;

    var second = new RideFitRecorder();
    second.StartOrResume(0);
    var secondPath = second.Stop()!;

    if (firstPath == secondPath || !System.IO.File.Exists(firstPath) || !System.IO.File.Exists(secondPath))
        throw new Exception("Consecutive rides did not retain separate FIT files.");
    _ = new FitRideLoader().Load(firstPath);
    _ = new FitRideLoader().Load(secondPath);
});

static void InTemporaryFolder(Action action)
{
    var tempRoot = Path.GetFullPath(Path.GetTempPath());
    var folder = Path.GetFullPath(Path.Combine(tempRoot, "wodra-recording-tests-" + Guid.NewGuid()));
    if (!folder.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
        throw new Exception("Test directory escaped temporary root.");
    Directory.CreateDirectory(folder);
    GameBootstrap.Settings.Recording.OutputFolder = folder;
    try { action(); }
    finally { Directory.Delete(folder, recursive: true); }
}
