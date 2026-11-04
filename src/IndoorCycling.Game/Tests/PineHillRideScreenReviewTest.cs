using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;

/// <summary>Loads Pine Hill Test through the catalogue and inspects its production RideScreen scenery.</summary>
public partial class PineHillRideScreenReviewTest : Node
{
    private readonly string _output = System.Environment.GetEnvironmentVariable("WODRA_REVIEW_OUTPUT")
        ?? Path.Combine(Path.GetTempPath(), "WodraPineHillReview");
    private int _assertions;

    public override async void _Ready()
    {
        try
        {
            Directory.CreateDirectory(_output);
            var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate<Control>();
            AddChild(screen);
            await Frames(10);

            var profile = new RiderProfileRecord(Guid.NewGuid(), "Pine Hill Review", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, 74, 9, true, 50);
            await GameBootstrap.SaveProfileAsync(profile);
            await GameBootstrap.SetActiveProfileAsync(profile.Id);
            GameBootstrap.Settings.DummyTrainer.Mode = "Watts";
            GameBootstrap.Settings.DummyTrainer.TargetWatts = 1500;
            GameBootstrap.Settings.DummyTrainer.CadenceRpm = 95;
            GameBootstrap.Settings.DebugModeEnabled = true;
            GameBootstrap.Settings.PacerPresetId = PacerEffortPreset.DisabledId;
            GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(_output, "fit");
            GameBootstrap.Settings.Save();
            GameBootstrap.ConfigureFakeTrainerMode();

            var route = RouteCatalog.GetById("pine-hill-test")
                ?? throw new InvalidOperationException("Pine Hill Test did not load from the normal route catalogue.");
            var routeSelection = screen.GetNode<RouteSelectionPanelController>("RouteSelectionPanel");
            var hud = screen.GetNode<HudController>("HudController");
            routeSelection.Show();
            await Frames(4);
            var choice = routeSelection.FindChildren("RouteChoice", "Button", true, false)
                .OfType<Button>().SingleOrDefault(button => button.Text.StartsWith(route.Name + "\n", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Pine Hill Test is missing from the production route-selection catalogue.");
            choice.EmitSignal(BaseButton.SignalName.Pressed);
            var preview = routeSelection.GetNode<RouteElevationPreview>(
                "PanelContainer/RouteBrowserMargin/RouteBrowserLayout/Content/Preview/RouteOverview");
            var elevation = routeSelection.GetNode<RouteElevationPreview>(
                "PanelContainer/RouteBrowserMargin/RouteBrowserLayout/Content/Preview/ElevationProfile");
            Assert(routeSelection.SelectedRouteId == route.Id && ReferenceEquals(preview.Route, route)
                && ReferenceEquals(elevation.Route, route), "catalogue, route preview and elevation profile select the same package");
            var startRide = routeSelection.FindChildren("StartRide", "Button", true, false).OfType<Button>().Single();
            Assert(!startRide.Disabled, "production catalogue exposes an enabled Start Ride action");
            if (!IsHeadless) Capture("pine-route-selection.png");

            startRide.EmitSignal(BaseButton.SignalName.Pressed);
            await Settle(2);
            var session = screen.GetNode<RideSessionNode>("RideSessionNode");
            var world = screen.GetNode<RideWorldView>("RideWorldView");
            ShowRide(screen, routeSelection, hud);
            Assert(session.CurrentRoute?.Id == route.Id && world.RouteGeometry is { IsPointToPoint: false },
                "production RideScreen starts the closed Pine Hill loop from the normal route catalogue");
            var diagnostic = world.GetDiagnosticState();
            Assert(diagnostic.BuildingsLoaded > 0 && diagnostic.TreesLoaded > 0
                && diagnostic.TerrainChunksLoaded > 0 && diagnostic.BackgroundStreetsLoaded > 0,
                "OSM buildings, mapped trees, LINZ terrain and surrounding streets load in RideScreen");
            var authoredScene = world.GetNode<RouteSceneLayer>("RouteGeometry/VideoAuthoredScene");
            Assert(authoredScene.FeaturesRendered >= 18
                && authoredScene.AuthoredAssetFeatures == 3 && authoredScene.AuthoredAssetMeshInstances is > 0 and <= 32
                && authoredScene.RoadMarkingFeatures == 6,
                $"observations load three Blender assets ({authoredScene.AuthoredAssetFeatures}; {authoredScene.AuthoredAssetMeshInstances} mesh instances) and six route markings");
            var focalAssetTriangles = CountTriangles(authoredScene.GetNode<Node3D>("authored-give-way-intersection-scene"))
                + CountTriangles(authoredScene.GetNode<Node3D>("mature-broadleaf-left"))
                + CountTriangles(authoredScene.GetNode<Node3D>("mature-broadleaf-right"));
            Assert(focalAssetTriangles <= 330_000,
                $"photo-textured boarded fence, hipped house and two mature trees stay within 330k focal-scene triangles ({focalAssetTriangles:N0})");

            var checkpoints = new[] { 0d, 200d, 302d, 600d, 682d, 917d, 1100d, 1268d };
            foreach (var checkpoint in checkpoints)
                if (!IsHeadless)
                    await CaptureSourceStyleView(world, $"pine-route-camera-{checkpoint:F0}m.png", checkpoint, 0);

            var sceneLayer = world.GetNode<RouteSceneLayer>("RouteGeometry/VideoAuthoredScene");
            Assert(sceneLayer.FeaturesRendered >= 18 && sceneLayer.VisibleFeatureCount > 0,
                $"remapped route observations remain visible in the production world ({sceneLayer.VisibleFeatureCount}/{sceneLayer.FeaturesRendered})");
            var length = route.DistanceMeters;
            var totalTarget = length * 3 + 6;
            var previousDistance = session.CurrentRideState.DistanceMeters;
            var previousRider = world.GetDiagnosticState().RiderWorldPosition;
            var previousForward = world.GetDiagnosticState().RiderForward;
            var completedLaps = 0;
            var capturedBefore = new bool[3];
            var capturedAfter = new bool[3];
            var telemetry = new System.Text.StringBuilder(
                "elapsed_seconds,total_distance_meters,lap_number,lap_distance_meters,gradient_percent,speed_kph,heading_x,heading_y,heading_z,rider_x,rider_y,rider_z,camera_x,camera_y,camera_z\\r\\n");
            var rideTimer = Stopwatch.StartNew();
            while (session.CurrentRideState.DistanceMeters < totalTarget && rideTimer.Elapsed < TimeSpan.FromMinutes(8))
            {
                await Settle(.25);
                var state = session.CurrentRideState;
                if (!session.IsRideActive)
                    throw new InvalidOperationException($"RideScreen ended automatically at {state.DistanceMeters:F1} m.");
                if (!session.HasRideRecording)
                    throw new InvalidOperationException("FIT recording stopped while crossing a Pine Hill lap.");
                if (state.DistanceMeters < previousDistance || (state.ElapsedSeconds >= 10
                    && state.DistanceMeters <= previousDistance && state.VirtualSpeedKph < 1))
                    throw new InvalidOperationException($"The simulated trainer stopped advancing ride distance at {state.DistanceMeters:F1} m.");
                diagnostic = world.GetDiagnosticState();
                var lapDistance = world.RouteGeometry!.WrapDistance(state.DistanceMeters);
                var lapNumber = (int)Math.Floor(state.DistanceMeters / length) + 1;
                var cameraDistance = (diagnostic.RiderWorldPosition - diagnostic.CameraWorldPosition).Length();
                var riderStep = diagnostic.RiderWorldPosition.DistanceTo(previousRider);
                var routeStep = state.DistanceMeters - previousDistance;
                var headingDot = diagnostic.RiderForward.Normalized().Dot(previousForward.Normalized());
                if (!diagnostic.RiderWorldPosition.IsFinite() || !diagnostic.CameraWorldPosition.IsFinite()
                    || cameraDistance is < .5f or > 30 || routeStep < 0
                    || riderStep > Math.Max(20, routeStep * 1.35 + 8) || headingDot < -.85f
                    || !double.IsFinite(state.CurrentGradientPercent + lapDistance))
                    throw new InvalidOperationException($"Rider/camera continuity failed at {state.DistanceMeters:F1} m " +
                        $"(lap={lapNumber}, riderStep={riderStep:F2} m, routeStep={routeStep:F2} m, " +
                        $"headingDot={headingDot:F3}, camera={cameraDistance:F2} m).");

                for (var boundaryLap = 1; boundaryLap <= 3; boundaryLap++)
                {
                    var boundary = boundaryLap * length;
                    if (!capturedBefore[boundaryLap - 1] && state.DistanceMeters >= boundary - 5)
                    {
                        capturedBefore[boundaryLap - 1] = true;
                        if (!IsHeadless) Capture($"pine-loop-lap{boundaryLap}-before-seam.png");
                    }
                    if (!capturedAfter[boundaryLap - 1] && state.DistanceMeters >= boundary + 5)
                    {
                        capturedAfter[boundaryLap - 1] = true;
                        Assert(lapNumber == boundaryLap + 1 && lapDistance < 15,
                            $"lap {boundaryLap} wraps into lap {boundaryLap + 1} at the closed seam " +
                            $"(total={state.DistanceMeters:F1} m, wrapped={lapDistance:F1} m)");
                        Assert(session.IsRideActive && session.HasRideRecording,
                            $"ride and FIT recording remain active after lap {boundaryLap}");
                        var lapHeader = RouteGradientInset.FormatRouteHeader(route, state.DistanceMeters, lapNumber);
                        Assert(lapHeader.Contains($"LAP {boundaryLap + 1}", StringComparison.Ordinal),
                            $"route HUD lap indicator advances to lap {boundaryLap + 1}");
                        if (!IsHeadless) Capture($"pine-loop-lap{boundaryLap + 1}-after-seam.png");
                        completedLaps = boundaryLap;
                    }
                }

                telemetry.AppendLine($"{state.ElapsedSeconds:F3},{state.DistanceMeters:F3},{lapNumber},{lapDistance:F3}," +
                    $"{state.CurrentGradientPercent:F3},{state.VirtualSpeedKph:F3},{diagnostic.RiderForward.X:F5}," +
                    $"{diagnostic.RiderForward.Y:F5},{diagnostic.RiderForward.Z:F5},{diagnostic.RiderWorldPosition.X:F3}," +
                    $"{diagnostic.RiderWorldPosition.Y:F3},{diagnostic.RiderWorldPosition.Z:F3},{diagnostic.CameraWorldPosition.X:F3}," +
                    $"{diagnostic.CameraWorldPosition.Y:F3},{diagnostic.CameraWorldPosition.Z:F3}");
                previousDistance = state.DistanceMeters;
                previousRider = diagnostic.RiderWorldPosition;
                previousForward = diagnostic.RiderForward;
                if (!IsHeadless && !File.Exists(Path.Combine(_output, "pine-render-performance.csv"))
                    && state.DistanceMeters > 30)
                    await CaptureRenderPerformance("pine-render-performance.csv", TimeSpan.FromSeconds(3));
            }
            File.WriteAllText(Path.Combine(_output, "pine-three-lap-ride.csv"), telemetry.ToString());
            Assert(completedLaps == 3 && session.CurrentRideState.DistanceMeters >= totalTarget,
                $"simulated trainer completes three continuous laps (distance={session.CurrentRideState.DistanceMeters:F1} m)");
            Assert(session.IsRideActive && session.HasRideRecording
                && session.CurrentRideState.ElapsedSeconds > 60,
                "third lap remains an active ride with accumulating elapsed time and FIT recording");

            if (!IsHeadless) Capture("pine-loop-three-laps-complete.png");
            var rideMenu = screen.GetNode<RideMenuPanelController>("RideMenuPanel");
            rideMenu.ShowInRideMenu();
            await Frames(2);
            rideMenu.ShowEndRideConfirmation();
            await Frames(2);
            var confirmStop = rideMenu.FindChildren("", "Button", true, false).OfType<Button>()
                .Single(button => button.Text == "End Ride");
            confirmStop.EmitSignal(BaseButton.SignalName.Pressed);
            var stopWait = Stopwatch.StartNew();
            while (session.IsRideActive && stopWait.Elapsed < TimeSpan.FromSeconds(30)) await Frames(1);
            Assert(!session.IsRideActive && session.LastRecordingError is null
                && session.LastStoppedRecordingPath is { } fitPath && File.Exists(fitPath),
                "normal Ride Menu end action stops the ride and finalizes its FIT file");
            var fitFrames = new IndoorCycling.Core.Simulation.FitRideLoader().Load(session.LastStoppedRecordingPath!);
            Assert(fitFrames.Count > 30 && fitFrames[^1].DistanceMeters is { } fitDistance
                && fitDistance >= totalTarget - 20 && fitFrames[^1].Elapsed.TotalSeconds > 60,
                $"FIT records all three laps and continuing elapsed time ({fitFrames.Count} samples, " +
                $"{fitFrames.LastOrDefault()?.DistanceMeters:F1} m)");
            await Frames(8);
            Assert(screen.GetNode<RouteSelectionPanelController>("RouteSelectionPanel").Visible
                || screen.GetNode<RideHistoryPanelController>("RideHistoryPanel").Visible
                || screen.GetNode<StartPanelController>("StartPanel").Visible,
                "normal end-ride UI returns to the route, summary, or home screen");
            GD.Print($"[PineHillRideScreenReview] PASS {_assertions} production assertions; " +
                $"route={route.DistanceMeters:F1} m; laps=3; FIT={session.LastStoppedRecordingPath}; screenshots={_output}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"[PineHillRideScreenReview] FAIL: {error}");
            GetTree().Quit(1);
        }
    }

    private static bool IsHeadless => RenderingServer.GetCurrentRenderingDriverName().Contains("headless", StringComparison.OrdinalIgnoreCase)
        || RenderingServer.GetCurrentRenderingDriverName().Contains("dummy", StringComparison.OrdinalIgnoreCase)
        || DisplayServer.GetName().Contains("headless", StringComparison.OrdinalIgnoreCase);

    private static void ShowRide(Control screen, RouteSelectionPanelController routeSelection, HudController hud)
    {
        var startPanel = screen.GetNode<StartPanelController>("StartPanel");
        foreach (var window in startPanel.GetChildren().OfType<Window>()) window.Hide();
        startPanel.Hide();
        screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel").Hide();
        routeSelection.Hide();
        hud.Show();
        hud.SetDebugDisplayEnabled(false);
    }

    private async Task CaptureSourceStyleView(RideWorldView world, string filename,
        double routeChainageMeters, float yawOffsetDegrees)
    {
        var viewportSize = GetViewport().GetVisibleRect().Size;
        var viewport = new SubViewport { Name = "SourceStyleCaptureViewport",
            Size = new Vector2I((int)viewportSize.X,(int)viewportSize.Y),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(viewport);
        viewport.World3D = world.GetWorld3D();
        var camera = new Camera3D { Name = "CaptureSourceStyleCamera", Fov = 70 };
        viewport.AddChild(camera);
        var pose = world.RouteGeometry!.GetPoseAtDistance(routeChainageMeters);
        var position = world.ToGlobal(ProceduralRoad.ToGodot(pose.Position) + Vector3.Up * 1.45f);
        var forward = world.GlobalBasis * ProceduralRoad.ToGodot(pose.Forward);
        forward = forward.Rotated(Vector3.Up, Mathf.DegToRad(yawOffsetDegrees));
        camera.GlobalPosition = position;
        // The dash camera is near level with only a slight roadward pitch. Keeping the
        // horizon visible is essential for comparing Dunedin's hillside streetscape.
        camera.LookAt(position + forward * 30 + Vector3.Down * 5.5f, Vector3.Up);
        camera.Current = true;
        world.PlayerRider.Hide();
        await Frames(4);
        GD.Print($"[PineHillStaticView] chainage={routeChainageMeters:F1}m yaw={yawOffsetDegrees:F0}° " +
            $"camera={camera.GlobalPosition} forward={-camera.GlobalBasis.Z}");
        RenderingServer.ForceDraw(false);
        var image = viewport.GetTexture().GetImage();
        if (image.IsEmpty() || image.SavePng(Path.Combine(_output,filename)) != Error.Ok)
            throw new IOException($"Static comparison screenshot {filename} could not be saved.");
        world.PlayerRider.Show();
        viewport.QueueFree();
        world.GetNode<ChaseCamera>("ChaseCamera").MakeCurrent();
        await Frames(2);
    }

    private void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
        GD.Print($"[PineHillRideScreenReview] PASS: {message}");
    }

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task Settle(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private void Capture(string name)
    {
        RenderingServer.ForceDraw(false);
        var image = GetViewport().GetTexture().GetImage();
        if (image.IsEmpty()) throw new IOException($"Screenshot {name} was empty.");
        if (image.SavePng(Path.Combine(_output, name)) != Error.Ok)
            throw new IOException($"Could not save RideScreen screenshot {name}.");
    }

    private static int CountTriangles(Node root) => root.FindChildren("", "MeshInstance3D", true, false)
        .OfType<MeshInstance3D>()
        .Where(instance => instance.Mesh is not null)
        .Sum(instance => instance.Mesh!.GetFaces().Length / 3);

    private async Task CaptureRenderPerformance(string filename, TimeSpan duration)
    {
        var samples = new System.Collections.Generic.List<(double FrameMs, ulong Primitives, ulong DrawCalls,
            ulong VideoBytes, ulong TextureBytes, ulong BufferBytes)>();
        var timer = Stopwatch.StartNew();
        var previous = Stopwatch.GetTimestamp();
        while (timer.Elapsed < duration)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var now = Stopwatch.GetTimestamp();
            var frameMs = (now - previous) * 1000d / Stopwatch.Frequency;
            previous = now;
            samples.Add((frameMs,
                RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame),
                RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame),
                RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed),
                RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed),
                RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed)));
        }
        if (samples.Count == 0) throw new InvalidOperationException("No RideScreen performance samples were captured.");
        var output = new System.Text.StringBuilder("frame_ms,fps,primitives,draw_calls,video_mem_bytes,texture_mem_bytes,buffer_mem_bytes\r\n");
        foreach (var sample in samples)
            output.AppendLine($"{sample.FrameMs:F3},{1000d / Math.Max(.001,sample.FrameMs):F2},{sample.Primitives},{sample.DrawCalls},{sample.VideoBytes},{sample.TextureBytes},{sample.BufferBytes}");
        File.WriteAllText(Path.Combine(_output, filename), output.ToString());
        var orderedFrameTimes = samples.Select(sample => sample.FrameMs).Order().ToArray();
        double Percentile(double p) => orderedFrameTimes[Math.Clamp((int)Math.Ceiling(p * orderedFrameTimes.Length) - 1, 0, orderedFrameTimes.Length - 1)];
        GD.Print($"[PineHillPerformance] driver={RenderingServer.GetCurrentRenderingDriverName()} samples={samples.Count} " +
            $"fpsMedian={1000d/Percentile(.5):F1} fpsP95Frame={Percentile(.95):F2}ms " +
            $"maxPrimitives={samples.Max(sample=>sample.Primitives)} maxDrawCalls={samples.Max(sample=>sample.DrawCalls)} " +
            $"videoMemMaxMiB={samples.Max(sample=>sample.VideoBytes)/1048576d:F1} " +
            $"textureMemMaxMiB={samples.Max(sample=>sample.TextureBytes)/1048576d:F1} " +
            $"bufferMemMaxMiB={samples.Max(sample=>sample.BufferBytes)/1048576d:F1}");
    }
}
