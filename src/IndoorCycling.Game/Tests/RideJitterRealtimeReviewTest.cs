using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;

/// <summary>Real-time RideScreen frame and camera sampler for issue #93.</summary>
public partial class RideJitterRealtimeReviewTest : Node
{
    private readonly StringBuilder _csv = new("phase,elapsed_s,delta_ms,process_ms,physics_ms,distance_m,speed_kph,cadence_rpm,rider_x,rider_y,rider_z,camera_x,camera_y,camera_z,desired_x,desired_y,desired_z,look_x,look_y,look_z,camera_distance,bounds_x,bounds_y,bounds_z,bounds_w,bounds_h,bounds_d,safe_x,safe_y,safe_w,safe_h,stable_w,stable_h,stable_d,crank_phase,sampling_enabled,camera_view,clearance,draw_calls,render_objects\n");
    private RideSessionNode? _session;
    private RideWorldView? _world;
    private HudController? _hud;
    private string _phase = "setup";
    private bool _recording;
    private ulong _startedAt;

    public override async void _Ready()
    {
        try
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            DisplayServer.WindowSetSize(new Vector2I(1500, 900));
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Enabled);
            var output = Path.Combine(ProjectSettings.GlobalizePath("res://../.."), "artifacts", "jitter-issue-93", "realtime-review-issue93-fix");
            Directory.CreateDirectory(output);
            var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate();
            AddChild(screen);
            await Settle(30);

            GameBootstrap.Settings.DebugModeEnabled = true;
            GameBootstrap.Settings.DummyTrainer.Mode = "W/kg";
            GameBootstrap.Settings.DummyTrainer.WattsPerKg = 1.3;
            GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(output, "fit");
            GameBootstrap.Settings.DummyTrainer.CadenceRpm = 85;
            GameBootstrap.ConfigureFakeTrainerMode();
            _session = screen.GetNode<RideSessionNode>("RideSessionNode");
            _world = screen.GetNode<RideWorldView>("RideWorldView");
            if (!await _session.StartTrainerSessionAsync(RouteCatalog.GetById("dunedin-one-way-oval-gardens")))
                throw new InvalidOperationException("Could not start production RideScreen on the reported route.");
            var hud = screen.GetNode<HudController>("HudController");
            _hud = hud;
            hud.Show();
            hud.SetDebugDisplayEnabled(true);
            foreach (var window in screen.GetNode<StartPanelController>("StartPanel").GetChildren())
                if (window is Window popup) popup.Hide();
            screen.GetNode<StartPanelController>("StartPanel").Hide();
            _session.JumpToRouteDistance("280");
            _session.ConfirmDebugDistanceJump();
            hud.HideDebugJumpConfirmation();
            hud.SetRecordingState(false, false);
            _world.SelectCameraInspectionView(RiderCameraView.RearThreeQuarter);
            var warmupFrames = 0;
            while (warmupFrames < 3600 &&
                   (_session.CurrentRideState.DistanceMeters < 360 || _session.CurrentRideState.VirtualSpeedKph < 18))
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                warmupFrames++;
            }
            if (_session.CurrentRideState.DistanceMeters is < 360 or > 450 ||
                _session.CurrentRideState.VirtualSpeedKph is < 18 or > 23)
                throw new InvalidOperationException($"Warmup missed the diagnostic section/speed: distance={_session.CurrentRideState.DistanceMeters:F1}m, speed={_session.CurrentRideState.VirtualSpeedKph:F1}km/h.");

            var riders = FindAll<CyclistVisual>(_world).ToArray();
            if (riders.Length < 1 || riders.Any(rider => !rider.UsesImportedModel))
                throw new InvalidOperationException($"Expected imported production riders; found {riders.Length} riders, imported={riders.Count(r => r.UsesImportedModel)}.");
            var screenIndex = DisplayServer.WindowGetCurrentScreen();
            GD.Print($"[RideJitterReview] route={_session.CurrentRoute?.Id} riderCount={riders.Length} window={DisplayServer.WindowGetSize()} windowMode={DisplayServer.WindowGetMode()} screen={screenIndex} refreshHz={DisplayServer.ScreenGetRefreshRate(screenIndex)} vsync={DisplayServer.WindowGetVsyncMode()} renderer={RenderingServer.GetCurrentRenderingDriverName()} cpu={OS.GetName()} {OS.GetProcessorName()} gpu={RenderingServer.GetVideoAdapterName()}");

            // Alternating the same live ride makes cadence, steering, HUD and scenery
            // comparable while the baked animation update is isolated.
            await CapturePhase("animated-baseline", 12, output);
            foreach (var rider in riders) rider.RiderAnimationSamplingEnabled = false;
            await CapturePhase("skeletal-sampling-disabled", 12, output);
            foreach (var rider in riders) rider.RiderAnimationSamplingEnabled = true;
            await CapturePhase("animated-restart-steer-coast", 12, output, motion: true);

            var csvPath = Path.Combine(output, "ride-jitter-realtime.csv");
            File.WriteAllText(csvPath, _csv.ToString(), new UTF8Encoding(false));
            WriteSummary(csvPath, Path.Combine(output, "summary.txt"));
            GD.Print($"[RideJitterReview] PASS: captured live variable-frame RideScreen data at {csvPath}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"[RideJitterReview] FAIL: {error}");
            GetTree().Quit(1);
        }
    }

    public override void _Process(double delta)
    {
        if (!_recording || _session is null || _world is null) return;
        var rider = _world.PlayerRider;
        var camera = _world.InspectionCamera;
        var state = _session.CurrentRideState;
        var bounds = rider.GetWorldVisualBounds();
        var stableBounds = rider.GetStableLocalVisualBounds();
        var safe = _hud?.GetCameraSafeViewportRect() ?? new Rect2(0, 0, 1, 1);
        var now = (Time.GetTicksUsec() - _startedAt) / 1_000_000d;
        var processMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000d;
        var physicsMs = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000d;
        _csv.Append(_phase).Append(',').Append(F(now)).Append(',').Append(F(delta * 1000d)).Append(',')
            .Append(F(processMs)).Append(',').Append(F(physicsMs)).Append(',').Append(F(state.DistanceMeters)).Append(',')
            .Append(F(state.VirtualSpeedKph)).Append(',').Append(state.CurrentCadenceRpm).Append(',')
            .Append(F(rider.GlobalPosition.X)).Append(',').Append(F(rider.GlobalPosition.Y)).Append(',').Append(F(rider.GlobalPosition.Z)).Append(',')
            .Append(F(camera.GlobalPosition.X)).Append(',').Append(F(camera.GlobalPosition.Y)).Append(',').Append(F(camera.GlobalPosition.Z)).Append(',')
            .Append(F(camera.DesiredPosition.X)).Append(',').Append(F(camera.DesiredPosition.Y)).Append(',').Append(F(camera.DesiredPosition.Z)).Append(',')
            .Append(F(camera.LookTargetPosition.X)).Append(',').Append(F(camera.LookTargetPosition.Y)).Append(',').Append(F(camera.LookTargetPosition.Z)).Append(',')
            .Append(F(camera.RiderToCameraDistance)).Append(',')
            .Append(F(bounds.Position.X)).Append(',').Append(F(bounds.Position.Y)).Append(',').Append(F(bounds.Position.Z)).Append(',')
            .Append(F(bounds.Size.X)).Append(',').Append(F(bounds.Size.Y)).Append(',').Append(F(bounds.Size.Z)).Append(',')
            .Append(F(safe.Position.X)).Append(',').Append(F(safe.Position.Y)).Append(',').Append(F(safe.Size.X)).Append(',').Append(F(safe.Size.Y)).Append(',')
            .Append(F(stableBounds.Size.X)).Append(',').Append(F(stableBounds.Size.Y)).Append(',').Append(F(stableBounds.Size.Z)).Append(',')
            .Append(F(rider.CrankAngleRadians / Math.Tau)).Append(',').Append(rider.RiderAnimationSamplingEnabled ? 1 : 0).Append(',')
            .Append(camera.View).Append(',').Append(camera.ClearanceResult).Append(',')
            .Append(Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)).Append(',')
            .Append(Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame)).AppendLine();
    }

    private async Task CapturePhase(string phase, int seconds, string output, bool motion = false)
    {
        _phase = phase;
        _startedAt = Time.GetTicksUsec();
        _recording = true;
        var began = Time.GetTicksMsec();
        var steeringDown = false;
        var coastApplied = false;
        var restartApplied = false;
        var higherPowerApplied = false;
        var normalPowerRestored = false;
        GD.Print($"[RideJitterReview] sample start phase={phase}, duration={seconds}s, routeDistance={_session!.CurrentRideState.DistanceMeters:F1}m, speed={_session.CurrentRideState.VirtualSpeedKph:F1}km/h, cadence={_session.CurrentRideState.CurrentCadenceRpm}rpm");
        while ((Time.GetTicksMsec() - began) < (ulong)seconds * 1000UL)
        {
            if (motion)
            {
                var elapsed = (Time.GetTicksMsec() - began) / 1000d;
                if (elapsed >= 3 && !steeringDown) { SendSteering(Key.Right, true); steeringDown = true; }
                else if (elapsed >= 6 && steeringDown) { SendSteering(Key.Right, false); steeringDown = false; }
                if (elapsed >= 1 && !higherPowerApplied) { _session.AdjustDummyPower(1); higherPowerApplied = true; }
                if (elapsed >= 3 && higherPowerApplied && !normalPowerRestored) { _session.AdjustDummyPower(-1); normalPowerRestored = true; }
                if (elapsed >= 7 && !coastApplied) { _session.AdjustDummyCadence(-85); coastApplied = true; }
                if (elapsed >= 9 && coastApplied && !restartApplied) { _session.AdjustDummyCadence(85); restartApplied = true; }
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        _recording = false;
        GD.Print($"[RideJitterReview] sample end phase={phase}, routeDistance={_session.CurrentRideState.DistanceMeters:F1}m, speed={_session.CurrentRideState.VirtualSpeedKph:F1}km/h, cadence={_session.CurrentRideState.CurrentCadenceRpm}rpm");
    }

    private void WriteSummary(string csvPath, string summaryPath)
    {
        var lines = File.ReadAllLines(csvPath).Skip(1).ToArray();
        var output = new StringBuilder();
        foreach (var phase in lines.Select(line => line.Split(',')).GroupBy(fields => fields[0]))
        {
            var rows = phase.ToArray();
            var delta = rows.Select(fields => N(fields, 2)).Order().ToArray();
            var boundsWidth = Range(rows, 24);
            var boundsHeight = Range(rows, 25);
            var boundsDepth = Range(rows, 26);
            var safeRect = Math.Max(Range(rows, 27), Math.Max(Range(rows, 28), Math.Max(Range(rows, 29), Range(rows, 30))));
            var stableBoundsRange = Math.Max(Range(rows, 31), Math.Max(Range(rows, 32), Range(rows, 33)));
            var relativeCamera = new[] { RangeRelative(rows, 11, 8), RangeRelative(rows, 12, 9), RangeRelative(rows, 13, 10) };
            var relativeDesired = new[] { RangeRelative(rows, 14, 8), RangeRelative(rows, 15, 9), RangeRelative(rows, 16, 10) };
            var cameraDistanceRange = Range(rows, 20);
            var spikes = delta.Count(value => value >= 33.33);
            var slowFrames = delta.Count(value => value > 17.0);
            output.AppendLine($"{phase.Key}: frames={delta.Length}, dt p50/p95/p99/max={Pct(delta,.50):F2}/{Pct(delta,.95):F2}/{Pct(delta,.99):F2}/{delta[^1]:F2} ms, >17ms={slowFrames}, >=33.33ms={spikes}");
            output.AppendLine($"  actual world-bounds range m: width={boundsWidth:F4}, height={boundsHeight:F4}, depth={boundsDepth:F4}; cached root-local bounds range={stableBoundsRange:F6}; safe-rect component range={safeRect:F5}");
            output.AppendLine($"  desired camera-rider world-offset ranges m={relativeDesired[0]:F4}/{relativeDesired[1]:F4}/{relativeDesired[2]:F4}; camera-rider world-offset ranges m={relativeCamera[0]:F4}/{relativeCamera[1]:F4}/{relativeCamera[2]:F4}; camera-distance range={cameraDistanceRange:F4}m");
        }
        File.WriteAllText(summaryPath, output.ToString(), new UTF8Encoding(false));
    }

    private static double Pct(double[] sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    private static string F(double value) => value.ToString("F6", CultureInfo.InvariantCulture);
    private static double N(string[] fields, int index) => double.Parse(fields[index], CultureInfo.InvariantCulture);
    private static double Range(string[][] rows, int index) => rows.Max(row => N(row, index)) - rows.Min(row => N(row, index));
    private static double RangeRelative(string[][] rows, int camera, int rider) =>
        rows.Max(row => N(row, camera) - N(row, rider)) - rows.Min(row => N(row, camera) - N(row, rider));
    private static T[] FindAll<T>(Node node) where T : Node
    {
        var found = node is T match ? new[] { match } : Array.Empty<T>();
        return found.Concat(node.GetChildren().SelectMany(FindAll<T>)).ToArray();
    }
    private static void SendSteering(Key key, bool pressed) => Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = pressed });
    private async Task Settle(int frames) { for (var i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
}
