using Godot;
using IndoorCycling.Core.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NumericsVector3 = System.Numerics.Vector3;

/// <summary>Production scene review for the one-way Signal Hill MTB showcase and environment resets.</summary>
public partial class MountainBikeRideScreenReviewTest : Node
{
    private readonly string _output = System.Environment.GetEnvironmentVariable("WODRA_REVIEW_OUTPUT")
        ?? Path.Combine(Path.GetTempPath(), "WodraMountainBikeRideScreenReview");
    private int _assertions;
    private Control? _reviewScreen;

    public override async void _Ready()
    {
        try
        {
            Directory.CreateDirectory(_output);
            _reviewScreen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate<Control>();
            var screen = _reviewScreen!;
            AddChild(screen);
            await Frames(8);
            var testProfile = new RiderProfileRecord(Guid.NewGuid(), "MTB Review Test",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 74, 9, true, 50);
            await GameBootstrap.SaveProfileAsync(testProfile);
            await GameBootstrap.SetActiveProfileAsync(testProfile.Id);
            GameBootstrap.Settings.DummyTrainer.Mode = "Watts";
            GameBootstrap.Settings.DummyTrainer.TargetWatts = 1500;
            GameBootstrap.Settings.DummyTrainer.CadenceRpm = 95;
            GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(_output, "fit");
            GameBootstrap.Settings.Save();
            GameBootstrap.ConfigureFakeTrainerMode();

            var session = screen.GetNode<RideSessionNode>("RideSessionNode");
            var world = screen.GetNode<RideWorldView>("RideWorldView");
            var hud = screen.GetNode<HudController>("HudController");
            var road = RouteCatalog.GetById("dunedin-one-way-oval-gardens")
                ?? throw new InvalidOperationException("Bundled road route is unavailable.");
            var mountainBike = RouteCatalog.GetById("signal-hill-big-easy-mtb")
                ?? throw new InvalidOperationException("Bundled Signal Hill MTB route is unavailable.");
            var gravel = RouteCatalog.GetById("otago-central-rail-trail-clyde-alexandra")
                ?? throw new InvalidOperationException("Bundled gravel route is unavailable.");
            var velodrome = RouteCatalog.GetById("wodra-velodrome-250")
                ?? throw new InvalidOperationException("Bundled velodrome route is unavailable.");

            Assert(mountainBike.RouteType == "Point-to-Point" && mountainBike.Environment == RouteEnvironment.MountainBike
                && mountainBike.Discipline == RouteDiscipline.MountainBike,
                "the bundled route keeps environment, discipline, and open route type separate");
            Assert(mountainBike.DistanceMeters is > 4500 and < 6500 && mountainBike.TotalAscentMeters is > 250 and < 400,
                "the selected mapped trail keeps its measured OSM distance and LINZ ascent");
            Assert(mountainBike.Points is { Count: > 300 } && mountainBike.SurfaceSegments.Count >= 4,
                "the MTB package includes dense route geometry and contiguous source surface sections");
            Assert(mountainBike.Points![0].Latitude != mountainBike.Points[^1].Latitude
                || mountainBike.Points[0].Longitude != mountainBike.Points[^1].Longitude,
                "the uphill trail has distinct natural start and end points");
            Assert(mountainBike.Capabilities.MaximumLateralOffsetMeters is > 0 and <= .30f
                && !mountainBike.Capabilities.SupportsPacer && !mountainBike.Capabilities.SupportsTraffic
                && !mountainBike.Capabilities.SupportsMultiplayer && !mountainBike.Capabilities.SupportsGhost
                && !mountainBike.Capabilities.SupportsCollectibles,
                "unsupported narrow-trail cyclist and collectible combinations remain disabled");
            Assert(!RouteEnvironmentPolicy.For(RouteEnvironment.MountainBike).SupportsAmbientActors
                && !RouteEnvironmentPolicy.For(RouteEnvironment.MountainBike).SupportsUrbanStreetScenery,
                "MTB disables creation of ambient cars, pedestrians, and urban street furniture");

            if (!await session.StartTrainerSessionAsync(road))
                throw new InvalidOperationException("Could not start production road dummy ride before MTB.");
            ShowRide(screen, hud);
            await Settle(1.5);
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/AmbientActors") is not null
                && world.LocalAiRiderCount > 0,
                "road traffic and local AI cyclists remain enabled before the MTB transition");
            await session.ExitToRouteSelectionAsync();

            var selection = screen.GetNode<RouteSelectionPanelController>("RouteSelectionPanel");
            selection.Show(); hud.Hide(); await Frames(4);
            var choice = selection.FindChildren("RouteChoice", "Button", true, false).OfType<Button>()
                .SingleOrDefault(button => button.Text.StartsWith(mountainBike.Name + "\n", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Signal Hill MTB is missing from the production route catalogue.");
            choice.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(3);
            var preview = selection.GetNode("PanelContainer/RouteBrowserMargin/RouteBrowserLayout/Content/Preview");
            Assert(selection.SelectedRouteId == mountainBike.Id
                && ReferenceEquals(preview.GetNode<RouteElevationPreview>("RouteOverview").Route, mountainBike)
                && ReferenceEquals(preview.GetNode<RouteElevationPreview>("ElevationProfile").Route, mountainBike),
                "route selection, map overview, elevation preview, and Start Ride share one route definition");
            Assert(!selection.FindChildren("PacerPresetPicker", "OptionButton", true, false).OfType<OptionButton>().Any(c => c.IsVisibleInTree())
                && !selection.FindChildren("RouteGhostChoiceRow", "HBoxContainer", true, false).OfType<Control>().Any(c => c.IsVisibleInTree()),
                "the singletrack selector does not offer pacers or a blank route-ghost row");
            Assert(RouteGradientInset.FormatRouteHeader(mountainBike, 1234, 1) == "ROUTE · 1.23 km",
                "the point-to-point inset does not present the climb as a lap");

            if (!IsHeadless)
            {
                foreach (var (size, fullscreen) in new[]
                {
                    (new Vector2I(960, 540), false), (new Vector2I(1152, 648), false), (new Vector2I(0, 0), true)
                })
                {
                    if (fullscreen) DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
                    else { DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed); DisplayServer.WindowSetSize(size); }
                    await Settle(.25);
                    Capture($"mtb-selector-{(fullscreen ? "fullscreen" : $"{size.X}x{size.Y}")}.png");
                    var panel = selection.GetNode<Control>("PanelContainer").GetGlobalRect();
                    var viewport = GetViewport().GetVisibleRect().Size;
                    Assert(panel.Position.X >= 0 && panel.Position.Y >= 0 && panel.End.X <= viewport.X && panel.End.Y <= viewport.Y,
                        $"MTB catalogue fits the {(fullscreen ? "fullscreen" : $"{size.X}x{size.Y}")} viewport");
                }
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetSize(new Vector2I(1152, 648));
            }

            var start = selection.FindChildren("StartRide", "Button", true, false).OfType<Button>().Single();
            Assert(!start.Disabled, "the point-to-point MTB route exposes an enabled Start Ride action");
            start.EmitSignal(BaseButton.SignalName.Pressed);
            await Settle(2);
            if (session.CurrentRoute?.Id != mountainBike.Id)
                throw new InvalidOperationException("The production Start Ride action did not start the selected MTB route.");
            ShowRide(screen, hud); await Settle(1.5);

            var geometry = world.RouteGeometry ?? throw new InvalidOperationException("MTB visual route failed to load.");
            var player = world.PlayerRider ?? throw new InvalidOperationException("Production MTB rider is missing.");
            var camera = world.InspectionCamera ?? throw new InvalidOperationException("Production MTB camera is missing.");
            Assert(geometry.IsPointToPoint && geometry.ClosureCorrection.LengthSquared() < 1e-6f,
                "the open centreline has no visual closure correction or synthetic lap seam");
            Assert(NumericsVector3.Distance(geometry.GetPoseAtDistance(mountainBike.DistanceMeters).Position,
                    geometry.GetPoseAtDistance(mountainBike.DistanceMeters + 100).Position) < .01f,
                "route pose sampling clamps beyond the natural endpoint instead of wrapping to the start");
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/MountainBikeSingletrackSurface") is not null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/TrailShoulderBed") is not null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/LINZTerrain") is not null,
                "the production scene loads its narrow dirt ribbon, edge bed, and bundled DEM terrain");
            var vegetation = world.GetNodeOrNull<VegetationLayer>("RouteGeometry/RouteVegetation");
            Assert(vegetation is not null && vegetation.TreesLoaded > 0,
                "the route loads bundled OSM-context vegetation without source-data folders");
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/AmbientActors") is null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/BackgroundStreets") is null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/RouteStreetFurniture") is null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/RoadMarkings") is null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/CollectibleCats") is null,
                "the production MTB scene instantiates no ambient traffic, urban road scenery, or unsupported collectibles");
            Assert(world.LocalAiRiderCount == 0 && world.PacerRiderState is null,
                "the production MTB ride has no local AI riders or pacers on the narrow path");
            Assert(player.GlobalPosition.IsFinite() && camera.GlobalPosition.IsFinite()
                && Math.Abs(player.GlobalPosition.Y - geometry.GetPoseAtDistance(session.CurrentRideState.DistanceMeters).Position.Y) < 1.5f,
                "the rider begins aligned to the route centreline and terrain elevation");

            if (!IsHeadless)
            {
                foreach (var (size, debug) in new[]
                {
                    (new Vector2I(960, 540), false), (new Vector2I(1152, 648), false),
                    (new Vector2I(1152, 648), true), (new Vector2I(1920, 1080), true)
                })
                {
                    DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                    DisplayServer.WindowSetSize(size); hud.SetDebugDisplayEnabled(debug); await Settle(.35);
                    Capture($"mtb-ride-{size.X}x{size.Y}-debug-{debug}.png");
                    Assert(player.GlobalPosition.IsFinite() && camera.GlobalPosition.IsFinite(),
                        $"production MTB camera and rider remain finite at {size.X}x{size.Y}, debug={debug}");
                }
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
                hud.SetDebugDisplayEnabled(false); await Settle(.4); Capture("mtb-ride-fullscreen.png");
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetSize(new Vector2I(1152, 648));
            }
            hud.SetDebugDisplayEnabled(false);

            var motionPath = Path.Combine(_output, "signal-hill-mtb-motion.csv");
            File.WriteAllText(motionPath, "elapsed_seconds,ride_distance_m,route_chainage_m,grade_percent,speed_kph,rider_x,rider_y,rider_z,camera_x,camera_y,camera_z" + System.Environment.NewLine);
            var startTicks = Time.GetTicksMsec();
            ulong lastSampleTicks = startTicks;
            var nextCapture = 0;
            var captureAt = new[] { .02, .08, .20, .35, .50, .65, .80, .93, 1.0 };
            const ulong maximumDurationMs = 1_200_000;
            var previousCamera = camera.GlobalPosition;
            double maximumCameraStep = 0;
            while (session.LastCompletedActivityDraft is null)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var now = Time.GetTicksMsec();
                var ride = session.CurrentRideState;
                var pose = geometry.GetPoseAtDistance(ride.DistanceMeters);
                if (now - lastSampleTicks >= 1_000)
                {
                    File.AppendAllText(motionPath,
                        $"{ride.ElapsedSeconds:F3},{ride.DistanceMeters:F3},{pose.LoopDistanceMeters:F3},{ride.CurrentGradientPercent:F3},{ride.VirtualSpeedKph:F3}," +
                        $"{player.GlobalPosition.X:F3},{player.GlobalPosition.Y:F3},{player.GlobalPosition.Z:F3}," +
                        $"{camera.GlobalPosition.X:F3},{camera.GlobalPosition.Y:F3},{camera.GlobalPosition.Z:F3}{System.Environment.NewLine}");
                    maximumCameraStep = Math.Max(maximumCameraStep, previousCamera.DistanceTo(camera.GlobalPosition));
                    previousCamera = camera.GlobalPosition;
                    lastSampleTicks = now;
                }
                var progress = ride.DistanceMeters / mountainBike.DistanceMeters;
                if (nextCapture < captureAt.Length && progress >= captureAt[nextCapture])
                {
                    GD.Print($"[MountainBikeRideScreenReview] CHECKPOINT {progress:P0} distance={ride.DistanceMeters:F1} m grade={ride.CurrentGradientPercent:F2}%");
                    if (!IsHeadless) Capture($"mtb-motion-{progress:P0}.png");
                    nextCapture++;
                }
                if (now - startTicks > maximumDurationMs)
                    throw new TimeoutException($"Dummy MTB ride did not naturally finish the {mountainBike.DistanceMeters:F0} m route; progress={ride.DistanceMeters:F0} m.");
            }

            var completed = session.LastCompletedActivityDraft;
            Assert(completed is { Status: RideActivityStatus.Completed, RouteId: "signal-hill-big-easy-mtb" }
                && completed.DistanceMeters >= mountainBike.DistanceMeters
                && completed.DistanceMeters <= mountainBike.DistanceMeters + 50,
                "the natural endpoint ends and saves the route once at its measured distance without a silent return leg");
            Assert(completed!.FitFilePath is { } fit && File.Exists(fit)
                && completed.ElevationGainMeters is > 250 and < 400,
                "the completed isolated ride finalizes its FIT and retains the route's measured climbing");
            Assert(maximumCameraStep < 50,
                $"the follow camera moves continuously during the full route (largest one-second step {maximumCameraStep:F1} m)");
            GD.Print($"[MountainBikeRideScreenReview] motion trace={motionPath}; distance={completed.DistanceMeters:F2} m; duration={completed.DurationSeconds:F1} s");

            await session.ExitToRouteSelectionAsync();
            if (!await session.StartTrainerSessionAsync(gravel)) throw new InvalidOperationException("Could not start gravel after MTB.");
            ShowRide(screen, hud); await Settle(1.5);
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/GravelTrailSurface") is not null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/MountainBikeSingletrackSurface") is null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/AmbientActors") is null,
                "MTB → gravel clears singletrack visuals and keeps ambient actors disabled");
            await session.ExitToRouteSelectionAsync();
            if (!await session.StartTrainerSessionAsync(velodrome)) throw new InvalidOperationException("Could not start velodrome after gravel.");
            ShowRide(screen, hud); await Settle(1.5);
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/VelodromeEnvironment") is not null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/MountainBikeSingletrackSurface") is null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/LINZTerrain") is null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/AmbientActors") is null,
                "gravel → velodrome clears trail scenery and leaves ambient cars and pedestrians uninstantiated");
            await session.ExitToRouteSelectionAsync();
            if (!await session.StartTrainerSessionAsync(road)) throw new InvalidOperationException("Could not restart road after MTB environments.");
            ShowRide(screen, hud); await Settle(1.5);
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/AmbientActors") is not null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/VelodromeEnvironment") is null
                && world.GetNodeOrNull<Node3D>("RouteGeometry/MountainBikeSingletrackSurface") is null,
                "road → MTB → gravel → velodrome → road restores road traffic and clears all environment-only geometry");
            if (!IsHeadless) Capture("road-after-mtb-gravel-track.png");
            await session.ExitToRouteSelectionAsync();
            screen.QueueFree();
            await Frames(2);
            _reviewScreen = null;
            GD.Print($"[MountainBikeRideScreenReview] PASS {_assertions} assertions; complete dummy route; visual artifacts={_output}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            if (_reviewScreen is not null)
            {
                _reviewScreen.QueueFree();
                await Frames(2);
                _reviewScreen = null;
            }
            GD.PushError($"[MountainBikeRideScreenReview] FAIL: {error}");
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
        GD.Print($"[MountainBikeRideScreenReview] PASS: {message}");
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
