using Godot;
using IndoorCycling.Core.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

/// <summary>Production RideScreen route-transition and rendered track review with isolated dummy data.</summary>
public partial class VelodromeRideScreenReviewTest : Node
{
    private readonly string _output = Path.Combine(Path.GetTempPath(), "WodraVelodromeRideScreenReview");
    private int _assertions;

    public override async void _Ready()
    {
        try
        {
            Directory.CreateDirectory(_output);
            var ride = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate<Control>();
            AddChild(ride);
            await Settle(.5);
            GameBootstrap.Settings.DummyTrainer.Mode = "Watts";
            GameBootstrap.Settings.DummyTrainer.TargetWatts = 1500;
            GameBootstrap.Settings.DummyTrainer.CadenceRpm = 95;
            GameBootstrap.ConfigureFakeTrainerMode();
            GameBootstrap.Settings.DebugModeEnabled = false;
            GameBootstrap.Settings.PacerPresetId = "moderate";
            GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(_output, "fit");
            var testProfile = new RiderProfileRecord(Guid.NewGuid(), "Velodrome Review Test",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 74, 9, true, 50);
            await GameBootstrap.SaveProfileAsync(testProfile);
            await GameBootstrap.SetActiveProfileAsync(testProfile.Id);

            var session = ride.GetNode<RideSessionNode>("RideSessionNode");
            var world = ride.GetNode<RideWorldView>("RideWorldView");
            var hud = ride.GetNode<HudController>("HudController");
            var road = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
            var track = RouteCatalog.GetById("wodra-velodrome-250")
                ?? throw new InvalidOperationException("Built-in velodrome route is missing.");

            if (!await session.StartTrainerSessionAsync(road)) throw new InvalidOperationException("Could not start the production road dummy ride.");
            ShowRide(ride, hud);
            await Settle(1.5);
            Assert(world.RouteGeometry is not null && session.CurrentRoute?.Id == road.Id, "production road route starts from its existing definition");
            Capture("road-before-track.png");
            await session.ExitToRouteSelectionAsync();

            if (!await session.StartTrainerSessionAsync(track, RideActivityMode.Flying200))
                throw new InvalidOperationException("Could not start the production velodrome dummy ride.");
            ShowRide(ride, hud);
            await Settle(2);
            var geometry = world.RouteGeometry ?? throw new InvalidOperationException("Velodrome route did not load.");
            Assert(session.CurrentActivityMode == RideActivityMode.Flying200, "selected route activity mode reaches the production ride");
            Assert(geometry.GetPoseAtDistance(15).BankSlope < .02f, "straight section stays visually level");
            Assert(Math.Abs(geometry.GetPoseAtDistance(85).BankSlope) > .16f, "turn section receives visual-only banking");
            Assert(track.GetGradientAt(85) == 0 && track.TotalAscentMeters == 0, "visual banking does not affect route grade or ascent");
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/VelodromeEnvironment") is not null,
                "track geometry is built by its environment renderer");
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/AmbientActors") is null &&
                world.GetNodeOrNull<Node3D>("RouteGeometry/BackgroundStreets") is null,
                "road traffic and scenery are absent on the velodrome");
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/CollectibleCats") is null && world.PacerRiderState is null &&
                session.GhostRider is null, "unsupported track pacers, ghosts and collectibles remain absent");
            Assert(world.LocalAiRiderCount == 0,
                "road AI rider traffic is absent on the velodrome");
            var effortStatus = hud.GetNode<Label>("TopHudPanel/MarginContainer/VBoxContainer/TrackEffortStatus");
            Assert(effortStatus.IsVisibleInTree() &&
                (effortStatus.Text.Contains("50 m", StringComparison.Ordinal) || effortStatus.Text.Contains("Flying 200 m", StringComparison.Ordinal)),
				$"track HUD displays run-up or live flying-effort status ({effortStatus.Text})");
            var player = world.PlayerRider ?? throw new InvalidOperationException("Production rider visual is missing.");
            Assert(player.GlobalPosition.IsFinite(), "track rider has a finite rendered transform");

            if (!IsHeadless)
            {
                foreach (var (size, debug) in new[]
                {
                    (new Vector2I(960, 540), false), (new Vector2I(1152, 648), true),
                    (new Vector2I(1152, 648), false), (new Vector2I(1920, 1080), true)
                })
                {
                    DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                    DisplayServer.WindowSetSize(size);
                    hud.SetDebugDisplayEnabled(debug);
                    await Settle(.4);
                    Capture($"velodrome-{size.X}x{size.Y}-debug-{debug}-start.png");
                }
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
                hud.SetDebugDisplayEnabled(false);
                await Settle(.5);
                Capture("velodrome-fullscreen.png");
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetSize(new Vector2I(1152, 648));
            }
            // Advance the actual RideScreen through multiple corners and the lap
            // seam in both rendered review and CI's headless production scene.
            for (var i = 0; i < 18; i++)
            {
                await Settle(1);
                if (!IsHeadless)
                {
                    Capture($"velodrome-motion-{i + 1:00}.png");
					File.AppendAllText(Path.Combine(_output, "motion.csv"),
						$"{session.CurrentRideState.ElapsedSeconds:F3},{session.CurrentRideState.DistanceMeters:F3},{player.GlobalPosition.X:F3},{player.GlobalPosition.Y:F3},{player.GlobalPosition.Z:F3},{world.InspectionCamera.GlobalPosition.X:F3},{world.InspectionCamera.GlobalPosition.Y:F3},{world.InspectionCamera.GlobalPosition.Z:F3}{System.Environment.NewLine}");
                }
            }

            var retry = hud.GetNode<Button>("TopHudPanel/MarginContainer/VBoxContainer/RetryTrackEffort");
            Assert(session.TrackEffortResult?.Completed == true && retry.IsVisibleInTree(),
                "a completed dummy flying effort presents its result and retry action");
            Assert(session.TrackEffortResult is { DistanceMeters: 200 } completed && completed.Splits.Count == 1 &&
                hud.GetNode<Label>("TopHudPanel/MarginContainer/VBoxContainer/TrackEffortStatus").Text.Contains("100 m", StringComparison.Ordinal) &&
                hud.GetNode<Label>("TopHudPanel/MarginContainer/VBoxContainer/TrackEffortStatus").Text.Contains("Finish", StringComparison.Ordinal),
                "the production flying result reports exact timed distance and its interpolated split");
            var pbProfileId = Guid.NewGuid();
            var pbKey = VelodromePersonalBestPolicy.Key(pbProfileId, track, RideActivityMode.Standing500);
            GameBootstrap.Settings.TrackPersonalBests[pbKey] = 41.23;
            GameBootstrap.Settings.Save();
            var reloadedSettings = AppSettings.LoadOrCreate();
            Assert(reloadedSettings.TrackPersonalBests.TryGetValue(pbKey, out var restoredTime) && Math.Abs(restoredTime - 41.23) < 1e-9,
                "track best settings survive a settings reload and remain keyed by profile");
            await session.RetryTrackEffortAsync();
            ShowRide(ride, hud);
            await Settle(.5);
            Assert(session.CurrentRoute?.Id == track.Id && session.CurrentActivityMode == RideActivityMode.Flying200 &&
                session.TrackEffortResult is null && session.CurrentRideState.DistanceMeters < 5,
                "retry restarts the same effort from its line with cleared result state");

            await session.ExitToRouteSelectionAsync();
            Assert(!effortStatus.IsVisibleInTree(), "track mode-specific HUD disappears when the ride ends");
            if (!await session.StartTrainerSessionAsync(road)) throw new InvalidOperationException("Could not restart the production road dummy ride after track.");
            ShowRide(ride, hud);
            await Settle(1.5);
            Assert(session.CurrentActivityMode == RideActivityMode.FreeRide && session.CurrentRoute?.Environment == RouteEnvironment.Road,
                "track to road transition resets activity mode and environment");
            Assert(!effortStatus.IsVisibleInTree(), "velodrome timing HUD is absent on the road");
            Assert(world.GetNodeOrNull<Node3D>("RouteGeometry/VelodromeEnvironment") is null &&
                world.GetNodeOrNull<Node3D>("RouteGeometry/AmbientActors") is not null,
                "road environment rebuild restores its traffic layer after track riding");
            Capture("road-after-track.png");
            await session.ExitToRouteSelectionAsync();

            GD.Print($"[VelodromeRideScreenReview] PASS: {_assertions} production assertions; screenshots and motion samples={_output}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"[VelodromeRideScreenReview] FAIL: {error}");
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
        GD.Print($"[VelodromeRideScreenReview] PASS: {message}");
    }
    private async Task Settle(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    private void Capture(string name)
    {
        if (IsHeadless) return;
        RenderingServer.ForceDraw(false);
        var image = GetViewport().GetTexture().GetImage();
        if (image.IsEmpty()) throw new IOException($"Screenshot {name} was empty.");
        var path = Path.Combine(_output, name);
        if (image.SavePng(path) != Error.Ok) throw new IOException($"Screenshot {name} could not be saved.");
    }
}
