using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

/// <summary>High-speed review of the production RideScreen camera and imported rider.</summary>
public partial class RiderContactCameraReviewTest : Node
{
    private static readonly RiderCameraView[] Views =
        [RiderCameraView.Side, RiderCameraView.FrontThreeQuarter];

    public override async void _Ready()
    {
        try
        {
            var output = Path.Combine(ProjectSettings.GlobalizePath("res://../.."), "artifacts",
                "alpha47-owner-review", "runtime-after");
            Directory.CreateDirectory(output);
            var metricsPath = Path.Combine(output, "camera-centering.jsonl");
            DisplayServer.WindowSetSize(new Vector2I(1500, 900));
            var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate();
            AddChild(screen);
            await Settle(12);

            GameBootstrap.Settings.DebugModeEnabled = true;
            GameBootstrap.Settings.DummyTrainer.Mode = "W/kg";
            GameBootstrap.Settings.DummyTrainer.WattsPerKg = 12;
            GameBootstrap.Settings.DummyTrainer.CadenceRpm = 85;
            GameBootstrap.ConfigureFakeTrainerMode();
            var session = screen.GetNode<RideSessionNode>("RideSessionNode");
            if (!await session.StartTrainerSessionAsync(RouteCatalog.GetById("dunedin-rolling-loop")))
                throw new InvalidOperationException("Could not start the Rolling City ride in production RideScreen.");
            var world = screen.GetNode<RideWorldView>("RideWorldView");
            var hud = screen.GetNode<HudController>("HudController");
            hud.Show();
            foreach (var window in screen.GetNode<StartPanelController>("StartPanel").GetChildren().OfType<Window>()) window.Hide();
            screen.GetNode<StartPanelController>("StartPanel").Hide();

            // The supplied marker was at 290 m and 61 km/h. Reproduce that segment
            // and the previously reported 740 m composition at high dummy output.
            foreach (var (jumpMeters, label) in new[] { (210.0, "marker003-segment"), (660.0, "740m-segment") })
            {
                session.JumpToRouteDistance(jumpMeters.ToString(System.Globalization.CultureInfo.InvariantCulture));
                session.ConfirmDebugDistanceJump();
                await Settle(480);
                GD.Print($"[RiderContactCameraReview] {label}: distance={session.CurrentRideState.DistanceMeters:F1}m speed={session.CurrentRideState.VirtualSpeedKph:F1}kph cadence={session.CurrentAnimationCadenceRpm}rpm");

                foreach (var size in new[] { new Vector2I(1500, 900), new Vector2I(1920, 1080) })
                foreach (var debug in new[] { true, false })
                {
                    DisplayServer.WindowSetSize(size);
                    hud.SetDebugDisplayEnabled(debug);
                    await Settle(12);
                    foreach (var view in Views)
                    {
                        world.SelectCameraInspectionView(view);
                        foreach (var settleFrames in new[] { 6, 30, 60, 120 })
                        {
                            await Settle(settleFrames);
                            var metric = Measure(world, hud, size, debug, view, label, session.CurrentRideState.DistanceMeters,
                                session.CurrentRideState.VirtualSpeedKph, settleFrames);
                            File.AppendAllText(metricsPath, JsonSerializer.Serialize(metric) + System.Environment.NewLine);
                            GD.Print($"[RiderContactCameraReview] {label} {size.X}x{size.Y} debug={debug} view={view} after={settleFrames}/60s subject=({metric.CenterX:F4},{metric.CenterY:F4}) safe=({metric.SafeCenterX:F4},{metric.SafeCenterY:F4}) offset=({metric.OffsetX:F4},{metric.OffsetY:F4}) speed={metric.SpeedKph:F1} cameraDrift={metric.CameraDriftMeters:F3} clearance={metric.Clearance}");
                        }
                        var final = Measure(world, hud, size, debug, view, label, session.CurrentRideState.DistanceMeters,
                            session.CurrentRideState.VirtualSpeedKph, 120);
                        if (Math.Abs(final.OffsetX) > .02f || Math.Abs(final.OffsetY) > .025f)
                            throw new InvalidOperationException($"Settled {view} framing missed the HUD-safe center by ({final.OffsetX:F4},{final.OffsetY:F4}) at {size}, debug={debug}, speed={final.SpeedKph:F1} kph.");
                        if (final.SubjectMinX < final.SafeMinX + .01f ||
                            final.SubjectMaxX > final.SafeMaxX - .01f ||
                            final.SubjectMinY < final.SafeMinY + .01f ||
                            final.SubjectMaxY > final.SafeMaxY - .01f)
                            throw new InvalidOperationException($"Settled {view} bounds leave the HUD-safe rectangle at {size}, debug={debug}: bounds=({final.SubjectMinX:F3},{final.SubjectMinY:F3})..({final.SubjectMaxX:F3},{final.SubjectMaxY:F3}) safe=({final.SafeMinX:F3},{final.SafeMinY:F3})..({final.SafeMaxX:F3},{final.SafeMaxY:F3}).");
                        await Save(output, $"{label}-{size.X}x{size.Y}-debug-{(debug ? "on" : "off")}-{view}.png");
                    }
                }
            }
            GD.Print($"[RiderContactCameraReview] PASS: actual RideScreen center metrics and captures: {output}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"[RiderContactCameraReview] FAIL: {error}");
            GetTree().Quit(1);
        }
    }

    private sealed record CameraMetric(string Segment, int Width, int Height, bool Debug, RiderCameraView View,
        int SettleFrames, double DistanceMeters, double SpeedKph, float CenterX, float CenterY,
        float SubjectMinX, float SubjectMinY, float SubjectMaxX, float SubjectMaxY,
        float SafeCenterX, float SafeCenterY, float OffsetX, float OffsetY,
        float SafeMinX, float SafeMinY, float SafeMaxX, float SafeMaxY,
        float CameraDriftMeters, string Clearance);

    private static CameraMetric Measure(RideWorldView world, HudController hud, Vector2I size, bool debug,
        RiderCameraView view, string segment, double distance, double speed, int settleFrames)
    {
        var camera = world.InspectionCamera;
        var bounds = world.PlayerRider.GetWorldVisualBounds();
        var viewport = camera.GetViewport().GetVisibleRect().Size;
        var safe = hud.GetCameraSafeViewportRect();
        var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        for (var i = 0; i < 8; i++)
        {
            var worldPoint = bounds.Position + new Vector3((i & 1) == 0 ? 0 : bounds.Size.X,
                (i & 2) == 0 ? 0 : bounds.Size.Y, (i & 4) == 0 ? 0 : bounds.Size.Z);
            var point = camera.UnprojectPosition(worldPoint) / viewport;
            min = min.Min(point);
            max = max.Max(point);
        }
        var subject = (min + max) * .5f;
        var safeCenter = safe.GetCenter();
        var safeEnd = safe.Position + safe.Size;
        var offset = subject - safeCenter;
        return new CameraMetric(segment, size.X, size.Y, debug, view, settleFrames, distance, speed,
            subject.X, subject.Y, min.X, min.Y, max.X, max.Y,
            safeCenter.X, safeCenter.Y, offset.X, offset.Y,
            safe.Position.X, safe.Position.Y, safeEnd.X, safeEnd.Y,
            camera.GlobalPosition.DistanceTo(camera.DesiredPosition), camera.ClearanceResult);
    }

    private async Task Save(string root, string name)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var image = GetViewport().GetTexture().GetImage();
        var path = Path.Combine(root, name);
        var error = image.SavePng(path);
        if (error != Error.Ok) throw new IOException($"Could not save {path}: {error}");
    }

    private async Task Settle(int frames)
    {
        for (var i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
