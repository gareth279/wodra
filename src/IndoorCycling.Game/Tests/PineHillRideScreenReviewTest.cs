using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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
            if (!IsHeadless) Capture("pine-route-start.png");

            var checkpoints = new[] { 395d, 775d, 1010d, 1372d, route.DistanceMeters - 0.1 };
            foreach (var checkpoint in checkpoints)
            {
                session.JumpToRouteDistance(checkpoint.ToString(CultureInfo.InvariantCulture));
                session.ConfirmDebugDistanceJump();
                hud.HideDebugJumpConfirmation();
                await Settle(1.25);
                diagnostic = world.GetDiagnosticState();
                Assert(diagnostic.RiderWorldPosition.IsFinite() && diagnostic.CameraWorldPosition.IsFinite()
                    && (diagnostic.RiderWorldPosition - diagnostic.CameraWorldPosition).Length() is > 0.5f and < 30,
                    $"rider and camera remain finite and in clearance range at {checkpoint:F0} m");
                File.AppendAllText(Path.Combine(_output, "pine-camera-checks.csv"),
                    $"{checkpoint:F1},{diagnostic.CameraClearanceResult},{diagnostic.CameraObstructionSource},{diagnostic.CameraRepeatedBlockedMovement}{System.Environment.NewLine}");
                if (!IsHeadless) Capture($"pine-route-{checkpoint:F0}m.png");
            }

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
}
