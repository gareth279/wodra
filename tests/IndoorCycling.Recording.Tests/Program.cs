using Dynastream.Fit;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;

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
    ("FIT round trip retains active distance and elevation totals", () => FitRoundTrip(false)),
    ("Stopping a paused FIT finalizes it without paused travel", () => FitRoundTrip(true)),
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

static void InTemporaryFolder(Action action)
{
    var tempRoot = Path.GetFullPath(Path.GetTempPath());
    var folder = Path.GetFullPath(Path.Combine(tempRoot, "gwift-recording-tests-" + Guid.NewGuid()));
    if (!folder.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
        throw new Exception("Test directory escaped temporary root.");
    Directory.CreateDirectory(folder);
    GameBootstrap.Settings.Recording.OutputFolder = folder;
    try { action(); }
    finally { Directory.Delete(folder, recursive: true); }
}
