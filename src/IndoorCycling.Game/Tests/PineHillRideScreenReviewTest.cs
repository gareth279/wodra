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
            Assert(session.CurrentRoute?.Id == route.Id && world.RouteGeometry is { IsPointToPoint: true },
                "production RideScreen starts the open GPS-derived route with its natural endpoint");
            var diagnostic = world.GetDiagnosticState();
            Assert(diagnostic.BuildingsLoaded > 0 && diagnostic.TreesLoaded > 0
                && diagnostic.TerrainChunksLoaded > 0 && diagnostic.BackgroundStreetsLoaded > 0,
                "OSM buildings, mapped trees, LINZ terrain and surrounding streets load in RideScreen");
            var authoredScene = world.GetNode<RouteSceneLayer>("RouteGeometry/VideoAuthoredScene");
            Assert(authoredScene.FeaturesRendered >= 20
                && authoredScene.AuthoredAssetFeatures == 3 && authoredScene.AuthoredAssetMeshInstances is > 0 and <= 32
                && authoredScene.RoadMarkingFeatures == 6,
                $"observations load three Blender assets ({authoredScene.AuthoredAssetFeatures}; {authoredScene.AuthoredAssetMeshInstances} mesh instances) and six route markings");
            var focalAssetTriangles = CountTriangles(authoredScene.GetNode<Node3D>("authored-give-way-intersection-scene"))
                + CountTriangles(authoredScene.GetNode<Node3D>("mature-broadleaf-left"))
                + CountTriangles(authoredScene.GetNode<Node3D>("mature-broadleaf-right"));
            Assert(focalAssetTriangles <= 330_000,
                $"photo-textured boarded fence, hipped house and two mature trees stay within 330k focal-scene triangles ({focalAssetTriangles:N0})");

            var checkpoints = new[] { 395d, 775d, 1010d, 1372d };
            foreach (var checkpoint in checkpoints)
            {
                session.JumpToRouteDistance(checkpoint.ToString(CultureInfo.InvariantCulture));
                session.ConfirmDebugDistanceJump();
                hud.HideDebugJumpConfirmation();
                await Settle(1.25);
                diagnostic = world.GetDiagnosticState();
                var riderCameraDistance = (diagnostic.RiderWorldPosition - diagnostic.CameraWorldPosition).Length();
                Assert(diagnostic.RiderWorldPosition.IsFinite() && diagnostic.CameraWorldPosition.IsFinite()
                    && riderCameraDistance is > 0.5f and < 30,
                    $"rider and camera remain finite and in clearance range at {checkpoint:F0} m " +
                    $"(distance={riderCameraDistance:F2}, rider={diagnostic.RiderWorldPosition}, camera={diagnostic.CameraWorldPosition}, " +
                    $"desired={diagnostic.CameraDesiredPosition}, clearance={diagnostic.CameraClearanceResult}, obstruction={diagnostic.CameraObstructionSource})");
                File.AppendAllText(Path.Combine(_output, "pine-camera-checks.csv"),
                    $"{checkpoint:F1},{diagnostic.CameraClearanceResult},{diagnostic.CameraObstructionSource},{diagnostic.CameraRepeatedBlockedMovement}{System.Environment.NewLine}");
                if (!IsHeadless)
                {
                    if (Math.Abs(checkpoint - 1372) < 1)
                    {
                        Capture("pine-route-1372m-gameplay-improved.png");
                        foreach (var viewChainage in new[] { 1362d, 1368.5d, 1371.735d, 1377.37d })
                        {
                            var filename = Math.Abs(viewChainage - 1377.37) < .01
                                ? "pine-route-1372m-static-gps-camera.png"
                                : Math.Abs(viewChainage - 1371.735) < .01
                                    ? "pine-route-1372m-static-route-camera.png"
                                : $"pine-route-approach-{viewChainage:F1}m-static.png";
                            await CaptureSourceStyleView(world, filename, viewChainage, 0);
                        }
                        await CaptureRenderPerformance("pine-render-performance.csv", TimeSpan.FromSeconds(3));
                    }
                }
                if (Math.Abs(checkpoint - 1372) < 1)
                {
                    var sceneLayer = world.GetNode<RouteSceneLayer>("RouteGeometry/VideoAuthoredScene");
                    Assert(sceneLayer.VisibleFeatureCount is > 0 && sceneLayer.VisibleFeatureCount < sceneLayer.FeaturesRendered,
                        $"route observations are chainage-culled at the 1,372 m scene ({sceneLayer.VisibleFeatureCount}/{sceneLayer.FeaturesRendered} visible)");
                }
            }

            // Move the dummy-trainer rider naturally from the final visual-review point to
            // the open endpoint. A debug teleport across that residential block tests the
            // camera's obstruction sweep against an impossible camera path.
            var nearFinishCaptured = false;
            var movingSamples = 0;
            while (session.CurrentRideState.DistanceMeters < route.DistanceMeters - 20 && movingSamples < 60)
            {
                await Settle(1);
                movingSamples++;
                diagnostic = world.GetDiagnosticState();
                var riderCameraDistance = (diagnostic.RiderWorldPosition - diagnostic.CameraWorldPosition).Length();
                Assert(diagnostic.RiderWorldPosition.IsFinite() && diagnostic.CameraWorldPosition.IsFinite()
                    && riderCameraDistance is > 0.5f and < 30,
                    $"moving dummy-trainer rider and chase camera remain clear near finish " +
                    $"(distance={riderCameraDistance:F2}, rider={diagnostic.RiderWorldPosition}, " +
                    $"camera={diagnostic.CameraWorldPosition}, desired={diagnostic.CameraDesiredPosition}, " +
                    $"clearance={diagnostic.CameraClearanceResult}, obstruction={diagnostic.CameraObstructionSource})");
                if (!nearFinishCaptured && session.CurrentRideState.DistanceMeters >= route.DistanceMeters - 25)
                {
                    if (!IsHeadless) Capture("pine-route-1680m.png");
                    nearFinishCaptured = true;
                }
            }
            Assert(session.CurrentRideState.DistanceMeters >= route.DistanceMeters - 20,
                $"dummy trainer rides naturally into the final 20 m (distance={session.CurrentRideState.DistanceMeters:F1} m)");

            Assert((world.RouteGeometry!.GetPoseAtDistance(0).Position
                - world.RouteGeometry.GetPoseAtDistance(route.DistanceMeters).Position).Length() > 300,
                "finish view remains separate from the start; there is no loop seam or teleport connector");
            await session.ExitToRouteSelectionAsync();
            GD.Print($"[PineHillRideScreenReview] PASS {_assertions} production assertions; route={route.DistanceMeters:F1} m; screenshots={_output}");
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
