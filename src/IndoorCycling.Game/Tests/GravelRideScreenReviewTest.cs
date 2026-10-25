using Godot;
using IndoorCycling.Core.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

/// <summary>Production RideScreen environment-transition, terrain, and gravel route review.</summary>
public partial class GravelRideScreenReviewTest : Node
{
    private readonly string _output = System.Environment.GetEnvironmentVariable("WODRA_REVIEW_OUTPUT")
        ?? Path.Combine(Path.GetTempPath(), "WodraGravelRideScreenReview");
    private int _assertions;

    public override async void _Ready()
    {
        try
        {
            Directory.CreateDirectory(_output);
            var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate<Control>();
            AddChild(screen);
            await Frames(8);
            var testProfile = new RiderProfileRecord(Guid.NewGuid(), "Gravel Review Test",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 74, 9, true, 50);
            await GameBootstrap.SaveProfileAsync(testProfile);
            await GameBootstrap.SetActiveProfileAsync(testProfile.Id);
            GameBootstrap.Settings.DummyTrainer.Mode = "Watts";
            GameBootstrap.Settings.DummyTrainer.TargetWatts = 1500;
            GameBootstrap.Settings.DummyTrainer.CadenceRpm = 95;
            var visualCheckpoints = System.Environment.GetEnvironmentVariable("WODRA_GRAVEL_VISUAL_CHECKPOINTS") == "1";
            GameBootstrap.Settings.DebugModeEnabled = visualCheckpoints;
            GameBootstrap.Settings.PacerPresetId = "moderate";
            GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(_output, "fit");
            GameBootstrap.Settings.Save();
            GameBootstrap.ConfigureFakeTrainerMode();

            var session = screen.GetNode<RideSessionNode>("RideSessionNode");
            var world = screen.GetNode<RideWorldView>("RideWorldView");
            var hud = screen.GetNode<HudController>("HudController");
            var road = RouteCatalog.GetById("dunedin-one-way-oval-gardens")
                ?? throw new InvalidOperationException("Bundled road route is unavailable.");
            var trail = RouteCatalog.GetById("otago-central-rail-trail-clyde-alexandra")
                ?? throw new InvalidOperationException("Bundled gravel route is unavailable.");
            var velodrome = RouteCatalog.GetById("wodra-velodrome-250")
                ?? throw new InvalidOperationException("Bundled velodrome route is unavailable.");

            if (!await session.StartTrainerSessionAsync(road))
                throw new InvalidOperationException("Could not start production road dummy ride.");
            ShowRide(screen, hud);
            await Settle(2);
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/AmbientActors") is not null,
                "road transition retains its ambient traffic layer");
            Assert(world.LocalAiRiderCount > 0, "road AI rider behavior remains available");
            await session.ExitToRouteSelectionAsync();

            GameFeaturesPreference.Set(true);
            var routeSelection = screen.GetNode<RouteSelectionPanelController>("RouteSelectionPanel");
            hud.Hide();
            routeSelection.Show();
            await Frames(2);
            var gravelChoice = routeSelection.FindChildren("RouteChoice", "Button", true, false)
                .OfType<Button>().SingleOrDefault(button => button.Text.StartsWith(trail.Name + "\n", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The gravel route is missing from the production route catalogue.");
            gravelChoice.EmitSignal(BaseButton.SignalName.Pressed);
            var browserRoot = routeSelection.GetNode("PanelContainer/RouteBrowserMargin/RouteBrowserLayout/Content/Preview");
            var overview = browserRoot.GetNode<RouteElevationPreview>("RouteOverview");
            var elevation = browserRoot.GetNode<RouteElevationPreview>("ElevationProfile");
            Assert(routeSelection.SelectedRouteId == trail.Id && ReferenceEquals(overview.Route, trail)
                && ReferenceEquals(elevation.Route, trail),
                "catalogue selection, route overview, and elevation preview use the same bundled gravel definition");
            var startRide = routeSelection.FindChildren("StartRide", "Button", true, false).OfType<Button>().Single();
            Assert(!startRide.Disabled && startRide.Text == "Start Ride",
                "selected gravel route exposes its enabled production Start Ride action");
            if (!IsHeadless)
            {
                foreach (var (size, debug) in new[]
                {
                    (new Vector2I(960, 540), false), (new Vector2I(1152, 648), true),
                    (new Vector2I(1920, 1080), false)
                })
                {
                    DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                    DisplayServer.WindowSetSize(size);
                    await Settle(.3);
                    var panelRect = routeSelection.GetNode<Control>("PanelContainer").GetGlobalRect();
                    var viewport = GetViewport().GetVisibleRect().Size;
                    GD.Print($"[GravelRideScreenReview] catalogue viewport={viewport} panel={panelRect}");
                    Assert(panelRect.Position.X >= 0 && panelRect.Position.Y >= 0
                        && panelRect.End.X <= viewport.X && panelRect.End.Y <= viewport.Y,
                        $"production route catalogue fits the {size.X}x{size.Y} viewport");
                    hud.SetDebugDisplayEnabled(debug);
                    await Settle(.25);
                    Capture($"gravel-route-selection-{size.X}x{size.Y}-debug-{debug}.png");
                }
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
                await Settle(.35);
                Capture("gravel-route-selection-fullscreen.png");
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetSize(new Vector2I(1152, 648));
                hud.SetDebugDisplayEnabled(false);
            }
            startRide.EmitSignal(BaseButton.SignalName.Pressed);
            await Settle(2);
            Assert(session.CurrentRoute?.Id == trail.Id,
                "production Start Ride launches the route currently selected in the catalogue");
            ShowRide(screen, hud);
            await Settle(2);

            var geometry = world.RouteGeometry ?? throw new InvalidOperationException("Gravel route geometry did not load.");
            Assert(session.CurrentRoute?.Id == trail.Id && trail.Environment == RouteEnvironment.Trail
                && trail.Discipline == RouteDiscipline.Gravel,
                "production RideScreen loads the selected route with separate Trail environment and Gravel discipline");
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/GravelTrailSurface") is not null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/TrailShoulderBed") is not null,
                "gravel centreline and shoulder surfaces render in the production world");
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/LINZTerrain") is not null,
                "bundled terrain loads without developer source-data folders");
            var vegetation = world.GetNodeOrNull<VegetationLayer>("RouteGeometry/RouteVegetation");
            Assert(vegetation is not null && vegetation.TreesLoaded > 0 && vegetation.TreesLoaded <= 250,
                "bundled mapped vegetation and bounded tree instances load");
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/AmbientActors") is null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/BackgroundStreets") is null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/RouteStreetFurniture") is null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/RoadMarkings") is null,
                "gravel creates neither ambient actors nor urban road scenery layers");
            Assert(world.LocalAiRiderCount == 0 && world.PacerRiderState is null,
                "unsupported narrow-trail AI and pacer combinations remain disabled");
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/CollectibleCats") is not null,
                "the existing Game features preference keeps supported trail collectibles enabled");
            Assert(trail.Capabilities.SupportsGhost && !trail.Capabilities.SupportsPacer
                && !trail.Capabilities.SupportsTraffic && !trail.Capabilities.SupportsMultiplayer,
                "trail retains route ghosts while unsupported cyclist traffic combinations stay off");
            Assert(trail.Points is { Count: > 100 } && trail.SurfaceSegments.Count == 10,
                "full loop and every route-relative source surface interval are available to the production route");
            var trailPoints = trail.Points ?? throw new InvalidOperationException("Gravel route has no source points.");
            Assert(trailPoints.First().Latitude == trailPoints.Last().Latitude
                && trailPoints.First().Longitude == trailPoints.Last().Longitude,
                "trail ends exactly at its lap seam after both endpoint reversals");
            Assert(trail.TotalAscentMeters is > 20 and < 35
                && trailPoints.Max(point => Math.Abs(point.GradientPercent ?? 0)) < 3,
                "the rendered ride uses the source terrain profile without bridge grade spikes");
            Assert(session.CurrentActivityMode == RideActivityMode.FreeRide,
                "gravel defaults to the shared free-ride mode rather than a route-specific activity mode");

            var player = world.PlayerRider ?? throw new InvalidOperationException("Production gravel rider visual is missing.");
            var startPose = geometry.GetPoseAtDistance(session.CurrentRideState.DistanceMeters);
            Assert(player.GlobalPosition.IsFinite() && Math.Abs(player.GlobalPosition.Y - startPose.Position.Y) < 1.5f,
                "player is rendered at the route surface elevation near the lap start");
            var inspectionCamera = world.InspectionCamera ?? throw new InvalidOperationException("Inspection camera is missing.");
            Assert(inspectionCamera.GlobalPosition.IsFinite(), "production follow camera has a finite trail transform");

            if (!IsHeadless)
            {
                foreach (var (size, debug) in new[]
                {
                    (new Vector2I(960, 540), false), (new Vector2I(1152, 648), false),
                    (new Vector2I(1152, 648), true), (new Vector2I(1920, 1080), true)
                })
                {
                    DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                    DisplayServer.WindowSetSize(size);
                    hud.SetDebugDisplayEnabled(debug);
                    await Settle(.35);
                    Capture($"gravel-{size.X}x{size.Y}-debug-{debug}.png");
                    Assert(player.GlobalPosition.IsFinite() && inspectionCamera.GlobalPosition.IsFinite(),
                        $"player and camera remain finite at {size.X}x{size.Y} with debug={debug}");
                }
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
                hud.SetDebugDisplayEnabled(false);
                await Settle(.5);
                Capture("gravel-fullscreen.png");
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetSize(new Vector2I(1152, 648));
            }

            if (visualCheckpoints && !IsHeadless)
            {
                hud.SetDebugDisplayEnabled(true);
                foreach (var checkpoint in new[] { 4_000, 8_000, 12_000 })
                {
                    session.JumpToRouteDistance(checkpoint.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    session.ConfirmDebugDistanceJump();
                    hud.HideDebugJumpConfirmation();
                    await Settle(1.25);
                    var pose = geometry.GetPoseAtDistance(session.CurrentRideState.DistanceMeters);
                    Assert(player.GlobalPosition.IsFinite() && Math.Abs(player.GlobalPosition.Y - pose.Position.Y) < 1.5f
                        && inspectionCamera.GlobalPosition.IsFinite(),
                        $"player and camera remain aligned after isolated visual reposition to {checkpoint} m");
                    Capture($"gravel-visual-checkpoint-{checkpoint}m.png");
                }
            }

            var fullLap = System.Environment.GetEnvironmentVariable("WODRA_GRAVEL_FULL_LAP") == "1";
            var motionPath = Path.Combine(_output, "gravel-motion.csv");
            File.WriteAllText(motionPath, "elapsed_s,ride_distance_m,route_distance_m,player_x,player_y,player_z,camera_x,camera_y,camera_z" + System.Environment.NewLine);
            var started = Time.GetTicksMsec();
            var lastSample = started;
            var nextCheckpoint = 0;
            var checkpoints = fullLap ? new[] { .25, .5, .75, 1.0 }
                : new[] { .02, .05 };
            var maximumDurationMs = fullLap ? 2_400_000UL : 20_000UL;
            while (true)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var now = Time.GetTicksMsec();
                var distance = session.CurrentRideState.DistanceMeters;
                if (now - lastSample >= 5_000)
                {
                    var pose = geometry.GetPoseAtDistance(distance);
                    File.AppendAllText(motionPath,
                        $"{session.CurrentRideState.ElapsedSeconds:F3},{distance:F3},{pose.LoopDistanceMeters:F3}," +
                        $"{player.GlobalPosition.X:F3},{player.GlobalPosition.Y:F3},{player.GlobalPosition.Z:F3}," +
                        $"{inspectionCamera.GlobalPosition.X:F3},{inspectionCamera.GlobalPosition.Y:F3},{inspectionCamera.GlobalPosition.Z:F3}{System.Environment.NewLine}");
                    lastSample = now;
                }
                var progress = distance / trail.DistanceMeters;
                if (nextCheckpoint < checkpoints.Length && progress >= checkpoints[nextCheckpoint])
                {
                    GD.Print($"[GravelRideScreenReview] CHECKPOINT {progress:P0} distance={distance:F1} m grade={trail.GetGradientAt(distance):F2}%");
                    if (!IsHeadless) Capture($"gravel-motion-{progress:P0}.png");
                    nextCheckpoint++;
                }
                if (fullLap && distance >= trail.DistanceMeters) break;
                if (!fullLap && now - started >= maximumDurationMs) break;
                if (now - started >= maximumDurationMs)
                    throw new TimeoutException($"Dummy gravel ride did not cover the complete {trail.DistanceMeters:F0} m lap before timeout ({distance:F0} m).");
            }
            if (fullLap)
                Assert(session.CurrentRideState.DistanceMeters >= trail.DistanceMeters,
                    "dummy-driven production RideScreen completed a full 16.136 km gravel lap");
            else
                Assert(session.CurrentRideState.DistanceMeters > startPose.LoopDistanceMeters + 5,
                    "dummy trainer advanced the player and follow camera along the trail centreline");

            await session.ExitToRouteSelectionAsync();
            if (!await session.StartTrainerSessionAsync(velodrome))
                throw new InvalidOperationException("Could not start production velodrome dummy ride after gravel.");
            ShowRide(screen, hud);
            await Settle(1.5);
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/VelodromeEnvironment") is not null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/GravelTrailSurface") is null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/LINZTerrain") is null,
                "gravel assets and environment are removed when transitioning to the velodrome");
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/AmbientActors") is null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/BackgroundStreets") is null
                && world.LocalAiRiderCount == 0,
                "velodrome keeps ambient cars, pedestrians and road AI absent after a gravel ride");
            Assert(hud.GetNode<Label>("TopHudPanel/MarginContainer/VBoxContainer/TrackEffortStatus").IsVisibleInTree(),
                "track HUD returns after gravel without residual route scenery or ride mode state");
            await session.ExitToRouteSelectionAsync();

            if (!await session.StartTrainerSessionAsync(road))
                throw new InvalidOperationException("Could not restart production road ride after velodrome.");
            ShowRide(screen, hud);
            await Settle(1.5);
            Assert(session.CurrentRoute?.Environment == RouteEnvironment.Road
                && world.GetNodeOrNull<Node3D>("RouteGeometry/AmbientActors") is not null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/VelodromeEnvironment") is null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/GravelTrailSurface") is null,
                "road → gravel → velodrome → road restores road scenery and clears environment-specific nodes");
            Assert(!hud.GetNode<Label>("TopHudPanel/MarginContainer/VBoxContainer/TrackEffortStatus").IsVisibleInTree(),
                "track-only HUD disappears after returning to road");
            if (!IsHeadless) Capture("road-after-gravel-velodrome.png");
            await session.ExitToRouteSelectionAsync();
            GD.Print($"[GravelRideScreenReview] PASS {_assertions} production assertions; fullLap={fullLap}; screenshots/motion={_output}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"[GravelRideScreenReview] FAIL: {error}");
            GetTree().Quit(1);
        }
    }

    private static bool IsHeadless => RenderingServer.GetCurrentRenderingDriverName().Contains("headless", StringComparison.OrdinalIgnoreCase)
        || RenderingServer.GetCurrentRenderingDriverName().Contains("dummy", StringComparison.OrdinalIgnoreCase)
        || DisplayServer.GetName().Contains("headless", StringComparison.OrdinalIgnoreCase);

    private static void ShowRide(Control screen, HudController hud)
    {
        var start = screen.GetNode<StartPanelController>("StartPanel");
        foreach (var child in start.GetChildren().OfType<Window>()) child.Hide();
        start.Hide();
        screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel").Hide();
        screen.GetNode<RouteSelectionPanelController>("RouteSelectionPanel").Hide();
        hud.Show();
    }

    private void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
        GD.Print($"[GravelRideScreenReview] PASS: {message}");
    }

    private async Task Settle(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void Capture(string name)
    {
        RenderingServer.ForceDraw(false);
        var image = GetViewport().GetTexture().GetImage();
        if (image.IsEmpty()) throw new IOException($"Screenshot {name} was empty.");
        var path = Path.Combine(_output, name);
        if (image.SavePng(path) != Error.Ok) throw new IOException($"Could not save screenshot {path}.");
    }
}
